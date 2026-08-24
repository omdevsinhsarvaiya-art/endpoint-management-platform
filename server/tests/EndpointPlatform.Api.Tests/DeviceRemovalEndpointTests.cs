using System.Net;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// The Remove Device flow — the existing offboard lifecycle exercised over real
/// HTTP against real PostgreSQL, end to end: authorization, credential
/// revocation, retirement, record preservation, audit, idempotent repeats, view
/// filtering, isolation from other devices, and the guarantee that a removed
/// machine cannot quietly return as an active duplicate.
/// </summary>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class DeviceRemovalEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static Uri OffboardOf(Guid deviceId) =>
        new($"/admin/v1/devices/{deviceId}/offboard", UriKind.Relative);

    private async Task<HttpClient> ClientAsync(string email)
    {
        var token = await _fixture.SignInAsync(email);
        return _fixture.CreateClientFor(token);
    }

    /// <summary>Seeds an Active device with one active agent credential.</summary>
    private async Task<(Guid DeviceId, Guid CredentialId)> SeedDeviceAsync(string hostname)
    {
        await using var db = _fixture.CreateDbContext();
        var organizationId = await db.Organizations.Select(o => o.Id).FirstAsync();

        var token = new EnrollmentToken(
            organizationId, $"removal-test-{Guid.CreateVersion7():N}",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                Guid.CreateVersion7().ToByteArray())),
            await db.PlatformUsers.Select(u => u.Id).FirstAsync(), "removal-test",
            DateTimeOffset.UtcNow.AddHours(1), 1);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            organizationId, hostname, $"smbios-{Guid.CreateVersion7()}", "1.1.0",
            "Windows 11 Pro", token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);

        var credential = new AgentCredential(
            device.Id,
            keyId: Guid.CreateVersion7().ToString("N"),
            secretHash: Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                Guid.CreateVersion7().ToByteArray())),
            issuedAt: DateTimeOffset.UtcNow);
        db.AgentCredentials.Add(credential);

        await db.SaveChangesAsync();
        return (device.Id, credential.Id);
    }

    private async Task<Device> ReloadAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.Devices.AsNoTracking().SingleAsync(d => d.Id == deviceId);
    }

    // ------------------------------------------------------------ the removal

    [Fact]
    public async Task Removal_retires_the_device_and_revokes_every_active_credential()
    {
        var (deviceId, credentialId) = await SeedDeviceAsync("REMOVE-1");
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        var response = await client.PostAsync(OffboardOf(deviceId), null);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var device = await ReloadAsync(deviceId);
        device.Status.ShouldBe(DeviceStatus.Retired);

        await using var db = _fixture.CreateDbContext();
        var credential = await db.AgentCredentials.AsNoTracking().SingleAsync(c => c.Id == credentialId);
        credential.RevokedAt.ShouldNotBeNull();
        (await db.AgentCredentials.CountAsync(c => c.DeviceId == deviceId && c.RevokedAt == null))
            .ShouldBe(0);
    }

    [Fact]
    public async Task The_record_and_its_history_are_preserved_never_deleted()
    {
        var (deviceId, _) = await SeedDeviceAsync("REMOVE-KEEP");
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        await client.PostAsync(OffboardOf(deviceId), null);

        // The row still exists, identity intact, and the detail endpoint still
        // serves it — removal is a lifecycle state, not an erasure.
        var device = await ReloadAsync(deviceId);
        device.Hostname.ShouldBe("REMOVE-KEEP");

        var detail = await client.GetAsync(new Uri($"/admin/v1/devices/{deviceId}", UriKind.Relative));
        detail.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await detail.Content.ReadAsStringAsync()).ShouldContain("Retired");
    }

    [Fact]
    public async Task Repeated_removal_is_idempotent()
    {
        var (deviceId, _) = await SeedDeviceAsync("REMOVE-TWICE");
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        (await client.PostAsync(OffboardOf(deviceId), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsync(OffboardOf(deviceId), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ReloadAsync(deviceId)).Status.ShouldBe(DeviceStatus.Retired);
    }

    [Fact]
    public async Task Removing_one_device_leaves_every_other_device_alone()
    {
        var (removed, _) = await SeedDeviceAsync("REMOVE-ME");
        var (bystander, bystanderCred) = await SeedDeviceAsync("BYSTANDER");
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        await client.PostAsync(OffboardOf(removed), null);

        var other = await ReloadAsync(bystander);
        other.Status.ShouldBe(DeviceStatus.Active);
        await using var db = _fixture.CreateDbContext();
        (await db.AgentCredentials.AsNoTracking().SingleAsync(c => c.Id == bystanderCred))
            .RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Removal_is_audited_with_the_revoked_credential_count()
    {
        var (deviceId, _) = await SeedDeviceAsync("REMOVE-AUDIT");
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        await client.PostAsync(OffboardOf(deviceId), null);

        await using var db = _fixture.CreateDbContext();
        var entries = await db.AuditLogEntries.AsNoTracking()
            .Where(e => e.DeviceId == deviceId && e.Action == "device.offboard")
            .Select(e => new { e.NewState, e.ActorDisplay })
            .ToListAsync();

        entries.ShouldNotBeEmpty();
        entries[0].NewState.ShouldNotBeNull();
        entries[0].NewState!.ShouldContain("Retired");
        entries[0].NewState!.ShouldContain("revokedCredentials");
        entries[0].ActorDisplay.ShouldBe(AdminApiPostgresFixture.ItAdminEmail);
    }

    // ---------------------------------------------------------------- views

    [Fact]
    public async Task A_removed_device_leaves_the_active_view_but_stays_in_the_retired_view()
    {
        var (deviceId, _) = await SeedDeviceAsync("REMOVE-VIEWS");
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        await client.PostAsync(OffboardOf(deviceId), null);

        var active = await client.GetStringAsync(
            new Uri("/admin/v1/devices?status=Active&search=REMOVE-VIEWS&pageSize=50", UriKind.Relative));
        active.ShouldNotContain("REMOVE-VIEWS");

        var retired = await client.GetStringAsync(
            new Uri("/admin/v1/devices?status=Retired&search=REMOVE-VIEWS&pageSize=50", UriKind.Relative));
        retired.ShouldContain("REMOVE-VIEWS");

        // And an unknown filter is a 400, never a silently empty page.
        (await client.GetAsync(new Uri("/admin/v1/devices?status=Sideways", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ----------------------------------------------------------- no duplicate

    [Fact]
    public async Task A_removed_machine_cannot_silently_become_an_active_duplicate()
    {
        var (deviceId, _) = await SeedDeviceAsync("REMOVE-NODUP");
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);
        await client.PostAsync(OffboardOf(deviceId), null);

        var retired = await ReloadAsync(deviceId);

        // The domain refuses re-enrollment of a retired device outright, and the
        // unique (organization, machine identifier) index makes a second row for
        // the same machine unrepresentable — together: no path back to Active
        // except an administrator's explicit reactivation.
        await using var db = _fixture.CreateDbContext();
        var tracked = await db.Devices.SingleAsync(d => d.Id == deviceId);
        Should.Throw<InvalidOperationException>(() => tracked.ReEnroll(
            retired.Hostname, "1.1.0", "Windows 11 Pro", Guid.CreateVersion7(), DateTimeOffset.UtcNow));

        var duplicate = Device.Enroll(
            retired.OrganizationId, retired.Hostname, retired.MachineIdentifier, "1.1.0",
            "Windows 11 Pro", retired.EnrolledWithTokenId, DateTimeOffset.UtcNow);
        db.Devices.Add(duplicate);
        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ------------------------------------------------------------------ rbac

    [Fact]
    public async Task Auditor_cannot_remove_a_device()
    {
        var (deviceId, credentialId) = await SeedDeviceAsync("REMOVE-AUDITOR");
        using var client = await ClientAsync(AdminApiPostgresFixture.AuditorEmail);

        var response = await client.PostAsync(OffboardOf(deviceId), null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReloadAsync(deviceId)).Status.ShouldBe(DeviceStatus.Active);
        await using var db = _fixture.CreateDbContext();
        (await db.AgentCredentials.AsNoTracking().SingleAsync(c => c.Id == credentialId))
            .RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Helpdesk_cannot_remove_a_device()
    {
        var (deviceId, _) = await SeedDeviceAsync("REMOVE-HELPDESK");
        using var client = await ClientAsync(AdminApiPostgresFixture.HelpdeskEmail);

        (await client.PostAsync(OffboardOf(deviceId), null))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReloadAsync(deviceId)).Status.ShouldBe(DeviceStatus.Active);
    }

    [Fact]
    public async Task An_unauthenticated_caller_cannot_remove_a_device()
    {
        var (deviceId, _) = await SeedDeviceAsync("REMOVE-ANON");
        using var client = _fixture.Factory.CreateClient();

        (await client.PostAsync(OffboardOf(deviceId), null))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReloadAsync(deviceId)).Status.ShouldBe(DeviceStatus.Active);
    }

    [Fact]
    public async Task Removing_an_unknown_device_is_a_not_found()
    {
        using var client = await ClientAsync(AdminApiPostgresFixture.ItAdminEmail);

        (await client.PostAsync(OffboardOf(Guid.CreateVersion7()), null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
