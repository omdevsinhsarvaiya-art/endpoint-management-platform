using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Enrollment;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Approving and rejecting machines that have asked to be managed.
/// </summary>
/// <remarks>
/// This is the authorization boundary of the enrollment flow: the agent-facing side
/// is anonymous by necessity, so everything that turns a request into a device has to
/// be proven here. These tests seed pending requests through the real
/// <see cref="PendingEnrollmentStore"/> against the fixture's real Redis, so the
/// atomicity and expiry behaviour under test is the behaviour that ships.
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class EnrollmentApprovalEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static readonly Uri PendingUri = new("/admin/v1/enrollments/pending", UriKind.Relative);

    private static Uri ApproveUri(string requestId) =>
        new($"/admin/v1/enrollments/{requestId}/approve", UriKind.Relative);

    private static Uri RejectUri(string requestId) =>
        new($"/admin/v1/enrollments/{requestId}/reject", UriKind.Relative);

    /// <summary>Mirrors what the agent does: keep a secret, publish only its digest.</summary>
    private static (string Secret, string RequestId) NewProof()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var requestId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        return (secret, requestId);
    }

    /// <summary>Seeds a pending request through the real store.</summary>
    private async Task<(string Secret, string RequestId)> SeedPendingAsync(string hostname = "TEST-PC")
    {
        var (secret, requestId) = NewProof();

        using var scope = _fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PendingEnrollmentStore>();

        var stored = await store.RequestAsync(
            requestId,
            new PendingEnrollment(
                MachineIdentifier: Guid.CreateVersion7().ToString(),
                Hostname: hostname,
                OperatingSystem: "Windows 11 Pro",
                AgentVersion: "1.0.0",
                RequestedAt: DateTimeOffset.UtcNow,
                Status: PendingEnrollmentStatus.Pending),
            CancellationToken.None);

        stored.ShouldBeTrue("the pending store must be reachable for these tests to mean anything");
        return (secret, requestId);
    }

    private async Task<PendingEnrollment?> ReadAsync(string requestId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PendingEnrollmentStore>();
        return await store.FindAsync(requestId, CancellationToken.None);
    }

    // ------------------------------------------------------ authentication

    [Fact]
    public async Task An_anonymous_caller_cannot_list_pending_enrollments()
    {
        using var client = _fixture.Factory.CreateClient();

        (await client.GetAsync(PendingUri)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_approve_or_reject()
    {
        // The whole point of approval gating: the anonymous side may ask, never decide.
        var (_, requestId) = await SeedPendingAsync();
        using var client = _fixture.Factory.CreateClient();

        (await client.PostAsync(ApproveUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsync(RejectUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await ReadAsync(requestId))!.Status.ShouldBe(PendingEnrollmentStatus.Pending);
    }

    [Fact]
    public async Task An_auditor_cannot_approve_an_enrollment()
    {
        // Approving creates a managed device; read-only roles must not be able to.
        var (_, requestId) = await SeedPendingAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.AuditorEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsync(ApproveUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadAsync(requestId))!.Status.ShouldBe(PendingEnrollmentStatus.Pending);
    }

    [Fact]
    public async Task An_auditor_cannot_list_pending_enrollments()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.AuditorEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.GetAsync(PendingUri)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // -------------------------------------------------------------- listing

    [Fact]
    public async Task An_administrator_sees_pending_machines_without_any_secret()
    {
        var (secret, requestId) = await SeedPendingAsync("LISTED-PC");
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.GetAsync(PendingUri);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("LISTED-PC");
        body.ShouldContain(requestId);

        // The proof secret must never be derivable from what an administrator sees.
        body.ShouldNotContain(secret);
        body.ShouldNotContain("sealedTokenSecret", Case.Insensitive);
        body.ShouldNotContain("requestSecret", Case.Insensitive);

        using var doc = JsonDocument.Parse(body);
        var entry = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("requestId").GetString() == requestId);
        entry.GetProperty("agentVersion").GetString().ShouldBe("1.0.0");
        entry.GetProperty("status").GetString().ShouldBe("Pending");
        entry.TryGetProperty("expiresAt", out _).ShouldBeTrue("an administrator needs to see the deadline");
    }

    // ------------------------------------------------------------ approval

    [Fact]
    public async Task Approving_marks_the_request_and_names_the_approver()
    {
        var (_, requestId) = await SeedPendingAsync("APPROVE-PC");
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PostAsync(ApproveUri(requestId), null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var stored = (await ReadAsync(requestId)).ShouldNotBeNull();
        stored.Status.ShouldBe(PendingEnrollmentStatus.Approved);
        stored.ApprovedBy.ShouldBe(AdminApiPostgresFixture.SuperAdminEmail);

        // The organization comes from the approver, never from the agent.
        stored.OrganizationId.ShouldNotBeNull();

        // The token that completes enrollment is sealed and stays server-side.
        stored.SealedTokenSecret.ShouldNotBeNull();
        (await response.Content.ReadAsStringAsync()).ShouldNotContain(stored.SealedTokenSecret!);
    }

    [Fact]
    public async Task Approving_creates_the_single_use_enrollment_token_the_claim_will_consume()
    {
        // Approval must feed the EXISTING enrollment path rather than invent a second
        // credential mechanism, so a real single-use token has to appear.
        var (_, requestId) = await SeedPendingAsync("TOKEN-PC");
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsync(ApproveUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = _fixture.CreateDbContext();
        var minted = db.EnrollmentTokens
            .Where(t => t.Name == "approved-enrollment-TOKEN-PC")
            .OrderByDescending(t => t.CreatedAt)
            .First();

        minted.MaxUses.ShouldBe(1, "an approval must not be reusable");
        minted.UseCount.ShouldBe(0);
        minted.IsRevoked.ShouldBeFalse();
    }

    [Fact]
    public async Task A_request_cannot_be_approved_twice()
    {
        var (_, requestId) = await SeedPendingAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsync(ApproveUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Double approval would mint a second credential for one decision.
        (await client.PostAsync(ApproveUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Concurrent_approvals_cannot_both_succeed()
    {
        var (_, requestId) = await SeedPendingAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var a = _fixture.CreateClientFor(token);
        using var b = _fixture.CreateClientFor(token);

        var results = await Task.WhenAll(
            a.PostAsync(ApproveUri(requestId), null),
            b.PostAsync(ApproveUri(requestId), null));

        results.Count(r => r.StatusCode == HttpStatusCode.OK)
            .ShouldBe(1, "the conditional transition must let exactly one approval win");
    }

    [Fact]
    public async Task An_unknown_or_expired_request_cannot_be_approved()
    {
        // Redis expiry makes "expired" and "never existed" the same observable state,
        // which is also what stops request ids being probed.
        var (_, unknownId) = NewProof();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsync(ApproveUri(unknownId), null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // ----------------------------------------------------------- rejection

    [Fact]
    public async Task Rejecting_is_terminal_and_issues_nothing()
    {
        var (_, requestId) = await SeedPendingAsync("REJECT-PC");
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsync(RejectUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var stored = (await ReadAsync(requestId)).ShouldNotBeNull();
        stored.Status.ShouldBe(PendingEnrollmentStatus.Rejected);
        stored.SealedTokenSecret.ShouldBeNull("a rejected machine must never get a token");

        // And it cannot be revived.
        (await client.PostAsync(ApproveUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.PostAsync(RejectUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task An_approved_request_cannot_then_be_rejected()
    {
        var (_, requestId) = await SeedPendingAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsync(ApproveUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsync(RejectUri(requestId), null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await ReadAsync(requestId))!.Status.ShouldBe(PendingEnrollmentStatus.Approved);
    }

    // --------------------------------------------------------------- audit

    [Fact]
    public async Task Approval_and_rejection_are_audited_without_leaking_material()
    {
        var (secret, approvedId) = await SeedPendingAsync("AUDIT-OK");
        var (_, rejectedId) = await SeedPendingAsync("AUDIT-NO");

        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        (await client.PostAsync(ApproveUri(approvedId), null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsync(RejectUri(rejectedId), null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = _fixture.CreateDbContext();

        var approved = db.AuditLogEntries
            .Where(e => e.Action == "enrollment.approved" && e.TargetId == approvedId)
            .OrderByDescending(e => e.OccurredAt)
            .First();
        approved.ActorDisplay.ShouldBe(AdminApiPostgresFixture.SuperAdminEmail);
        approved.TargetDisplay.ShouldBe("AUDIT-OK");
        (approved.NewState ?? "").ShouldNotContain(secret);

        db.AuditLogEntries
            .Any(e => e.Action == "enrollment.rejected" && e.TargetId == rejectedId)
            .ShouldBeTrue();
    }
}
