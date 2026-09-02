using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using EndpointPlatform.Domain.Agents;
using EndpointPlatform.Infrastructure.Software;
using EndpointPlatform.Infrastructure.Tests.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// The server-side publish gate, called the way an attacker would call it: over
/// HTTP, with no dashboard in between.
/// </summary>
/// <remarks>
/// <para>
/// Three properties, each held at the API rather than in the UI. The hash is the
/// server's, computed over the bytes it stored, and nothing the client says can
/// change it. The signer is the server's, read from the artifact's own signature,
/// and nothing the client types can change it. And publishing re-verifies both
/// against the bytes on disk at that moment, so a build that was fine at upload
/// and has since been tampered with is refused too.
/// </para>
/// <para>
/// Every signed artifact here is signed by the fixture's in-memory authority, which
/// the test host alone trusts. See <see cref="AdminApiPostgresFixture.SigningAuthority"/>.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class PublishGateTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static readonly Uri Releases = new("/admin/v1/agent-releases", UriKind.Relative);
    private static Uri Release(Guid id, string action) => new($"/admin/v1/agent-releases/{id}/{action}", UriKind.Relative);

    private async Task<HttpClient> AdminAsync() =>
        _fixture.CreateClientFor(await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail));

    private static string Seed(string version) => $"{version}-{Guid.CreateVersion7():N}";

    private static MultipartFormDataContent Form(byte[] bytes, string version, string? sha256 = null, string? signer = null)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var form = new MultipartFormDataContent
        {
            { file, "file", $"EndpointPlatformAgent-{version}-x64.msi" },
            { new StringContent(version), "version" },
        };
        if (sha256 is not null) form.Add(new StringContent(sha256), "sha256");
        if (signer is not null) form.Add(new StringContent(signer), "signerSubject");
        return form;
    }

    private static async Task<Guid> IdOf(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync();
        return Guid.Parse(JsonDocument.Parse(body).RootElement.GetProperty("releaseId").GetString()!);
    }

    private async Task<AgentRelease> RowAsync(Guid id)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AgentReleases.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    // ---- SHA-256 is the server's ---------------------------------------------

    /// <summary>The stored hash is computed over the stored bytes, whatever was declared.</summary>
    [Fact]
    public async Task The_recorded_hash_is_computed_by_the_server()
    {
        using var client = await AdminAsync();
        var bytes = TestArtifacts.SignedMsi(AdminApiPostgresFixture.SigningAuthority, seed: Seed("7.60.0"));

        // No hash declared at all.
        var id = await IdOf(await client.PostAsync(Releases, Form(bytes, "7.60.0")));

        (await RowAsync(id)).Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>A declared hash that disagrees with the bytes is a damaged upload, refused.</summary>
    [Fact]
    public async Task A_client_cannot_override_the_hash_with_a_wrong_declaration()
    {
        using var client = await AdminAsync();
        var bytes = TestArtifacts.SignedMsi(AdminApiPostgresFixture.SigningAuthority, seed: Seed("7.61.0"));

        var response = await client.PostAsync(Releases, Form(bytes, "7.61.0", sha256: new string('c', 64)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var db = _fixture.CreateDbContext();
        (await db.AgentReleases.AnyAsync(r => r.Version == "7.61.0")).ShouldBeFalse("nothing is recorded from a refused upload");
    }

    /// <summary>A declared hash that agrees is accepted -- and still not what is stored; the server's is.</summary>
    [Fact]
    public async Task A_correct_declared_hash_is_accepted_as_a_cross_check()
    {
        using var client = await AdminAsync();
        var bytes = TestArtifacts.SignedMsi(AdminApiPostgresFixture.SigningAuthority, seed: Seed("7.62.0"));
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var id = await IdOf(await client.PostAsync(Releases, Form(bytes, "7.62.0", sha256: sha.ToUpperInvariant())));

        (await RowAsync(id)).Sha256.ShouldBe(sha, "stored in the server's canonical lower-case form");
    }

    // ---- signer is the server's ---------------------------------------------

    /// <summary>A signer typed into the upload form is discarded; the artifact decides.</summary>
    [Fact]
    public async Task A_typed_signer_is_ignored_in_favour_of_the_verified_one()
    {
        using var client = await AdminAsync();

        var unsigned = await IdOf(await client.PostAsync(
            Releases, Form(TestArtifacts.UnsignedMsi(Seed("7.63.0")), "7.63.0", signer: "CN=Whoever I Say")));
        (await RowAsync(unsigned)).SignerSubject.ShouldBeNull();

        var signed = await IdOf(await client.PostAsync(
            Releases, Form(TestArtifacts.SignedMsi(AdminApiPostgresFixture.SigningAuthority, seed: Seed("7.64.0")), "7.64.0", signer: "CN=Whoever I Say")));
        (await RowAsync(signed)).SignerSubject.ShouldNotBeNull();
        (await RowAsync(signed)).SignerSubject!.ShouldContain(AdminApiPostgresFixture.ExpectedSignerSubject);
        (await RowAsync(signed)).SignerSubject!.ShouldNotContain("Whoever");
    }

    // ---- the gate -------------------------------------------------------------

    [Fact]
    public async Task An_unsigned_artifact_cannot_be_published()
    {
        using var client = await AdminAsync();
        var id = await IdOf(await client.PostAsync(Releases, Form(TestArtifacts.UnsignedMsi(Seed("7.65.0")), "7.65.0")));

        var response = await client.PostAsync(Release(id, "publish"), null);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await RowAsync(id)).Status.ShouldBe(AgentReleaseStatus.Draft);
    }

    /// <summary>Valid signature, trusted chain, right EKU -- wrong publisher.</summary>
    [Fact]
    public async Task An_artifact_signed_by_an_unexpected_publisher_cannot_be_published()
    {
        using var client = await AdminAsync();
        using var impostor = TestArtifacts.IssueLeaf(AdminApiPostgresFixture.SigningAuthority, "CN=Not Techsara Ltd");
        var bytes = TestArtifacts.SignedMsi(impostor, AdminApiPostgresFixture.SigningAuthority.Root, Seed("7.66.0"));

        var id = await IdOf(await client.PostAsync(Releases, Form(bytes, "7.66.0")));

        // Registered fine, and the real signer is recorded -- so the operator can
        // see exactly who did sign it -- but it will not publish.
        (await RowAsync(id)).SignerSubject.ShouldBeNull("only a signer that passes the full check is recorded as the release's signer");

        (await client.PostAsync(Release(id, "publish"), null))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await RowAsync(id)).Status.ShouldBe(AgentReleaseStatus.Draft);
    }

    /// <summary>Right publisher, but the certificate is not a code-signing one.</summary>
    [Fact]
    public async Task An_artifact_signed_with_a_non_code_signing_certificate_cannot_be_published()
    {
        using var client = await AdminAsync();
        using var tls = TestArtifacts.IssueLeaf(AdminApiPostgresFixture.SigningAuthority, AdminApiPostgresFixture.ExpectedSignerSubject, codeSigningEku: false);
        var bytes = TestArtifacts.SignedMsi(tls, AdminApiPostgresFixture.SigningAuthority.Root, Seed("7.67.0"));

        var id = await IdOf(await client.PostAsync(Releases, Form(bytes, "7.67.0")));

        (await client.PostAsync(Release(id, "publish"), null))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    /// <summary>
    /// The stored bytes are re-hashed at publish time. A build that was valid at
    /// upload and has since been altered on disk is refused, whatever its row says.
    /// </summary>
    [Fact]
    public async Task A_stored_artifact_modified_after_upload_cannot_be_published()
    {
        using var client = await AdminAsync();
        var bytes = TestArtifacts.SignedMsi(AdminApiPostgresFixture.SigningAuthority, seed: Seed("7.68.0"));
        var id = await IdOf(await client.PostAsync(Releases, Form(bytes, "7.68.0")));
        var row = await RowAsync(id);

        // Flip one byte of the stored blob, in place, under its content address.
        var directory = _fixture.Factory.Services.GetRequiredService<IOptions<PackageStorageOptions>>().Value.Directory;
        var path = Path.Combine(directory, row.Sha256 + ".bin");
        File.Exists(path).ShouldBeTrue("the artifact is stored under its hash");
        var stored = await File.ReadAllBytesAsync(path);
        stored[^1] ^= 0x01;
        await File.WriteAllBytesAsync(path, stored);

        var response = await client.PostAsync(Release(id, "publish"), null);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await RowAsync(id)).Status.ShouldBe(AgentReleaseStatus.Draft);
    }

    /// <summary>Everything right: signed by the expected publisher, bytes intact.</summary>
    [Fact]
    public async Task A_correctly_signed_artifact_publishes_and_records_its_signer()
    {
        using var client = await AdminAsync();
        var bytes = TestArtifacts.SignedMsi(AdminApiPostgresFixture.SigningAuthority, seed: Seed("7.69.0"));
        var id = await IdOf(await client.PostAsync(Releases, Form(bytes, "7.69.0")));

        (await client.PostAsync(Release(id, "publish"), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var row = await RowAsync(id);
        row.Status.ShouldBe(AgentReleaseStatus.Published);
        row.SignerSubject.ShouldNotBeNull();
        row.SignerSubject!.ShouldContain(AdminApiPostgresFixture.ExpectedSignerSubject);
    }

    /// <summary>The refusal explains itself without leaking anything about the bytes.</summary>
    [Fact]
    public async Task A_refusal_names_the_requirement_not_the_bytes()
    {
        using var client = await AdminAsync();
        var id = await IdOf(await client.PostAsync(Releases, Form(TestArtifacts.UnsignedMsi(Seed("7.70.0")), "7.70.0")));

        var body = await (await client.PostAsync(Release(id, "publish"), null)).Content.ReadAsStringAsync();

        body.ShouldContain("Authenticode");
        body.ShouldContain("SHA-256");
        body.ShouldNotContain("0x");
        body.ShouldNotContain("Exception");
    }

    // ---- replacing a draft's artifact ---------------------------------------

    /// <summary>
    /// The path by which an unsigned draft becomes its signed self: same row, new
    /// bytes, hash recomputed by the server over the new bytes.
    /// </summary>
    [Fact]
    public async Task A_drafts_artifact_can_be_replaced_and_the_hash_follows_the_new_bytes()
    {
        using var client = await AdminAsync();
        var unsigned = TestArtifacts.UnsignedMsi(Seed("7.71.0"));
        var id = await IdOf(await client.PostAsync(Releases, Form(unsigned, "7.71.0")));
        var before = await RowAsync(id);
        before.SignerSubject.ShouldBeNull();

        var signed = TestArtifacts.SignedMsi(AdminApiPostgresFixture.SigningAuthority, seed: Seed("7.71.0-signed"));
        var replace = await client.PutAsync(Release(id, "artifact"), Form(signed, "7.71.0"));
        replace.StatusCode.ShouldBe(HttpStatusCode.OK);

        var after = await RowAsync(id);
        after.Id.ShouldBe(id, "same release, not a second one");
        after.Version.ShouldBe("7.71.0");
        after.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(signed)));
        after.Sha256.ShouldNotBe(before.Sha256, "signing changes the bytes, so the hash must change with them");
        after.ContentSizeBytes.ShouldBe(signed.LongLength);
        after.SignerSubject.ShouldNotBeNull();

        // And now it publishes.
        (await client.PostAsync(Release(id, "publish"), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>A published release is immutable; its artifact cannot be swapped.</summary>
    [Fact]
    public async Task A_published_releases_artifact_cannot_be_replaced()
    {
        using var client = await AdminAsync();
        var id = await IdOf(await client.PostAsync(
            Releases, Form(TestArtifacts.SignedMsi(AdminApiPostgresFixture.SigningAuthority, seed: Seed("7.72.0")), "7.72.0")));
        (await client.PostAsync(Release(id, "publish"), null)).EnsureSuccessStatusCode();
        var published = await RowAsync(id);

        var response = await client.PutAsync(
            Release(id, "artifact"), Form(TestArtifacts.SignedMsi(AdminApiPostgresFixture.SigningAuthority, seed: Seed("7.72.0-other")), "7.72.0"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await RowAsync(id)).Sha256.ShouldBe(published.Sha256, "the published bytes are frozen");
    }

    [Fact]
    public async Task Replacing_an_unknown_releases_artifact_is_not_found()
    {
        using var client = await AdminAsync();

        (await client.PutAsync(
            Release(Guid.CreateVersion7(), "artifact"),
            Form(TestArtifacts.UnsignedMsi(Seed("7.73.0")), "7.73.0")))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>Replacement requires the same permission as publishing.</summary>
    [Fact]
    public async Task Helpdesk_cannot_replace_an_artifact()
    {
        using var admin = await AdminAsync();
        var id = await IdOf(await admin.PostAsync(Releases, Form(TestArtifacts.UnsignedMsi(Seed("7.74.0")), "7.74.0")));

        using var helpdesk = _fixture.CreateClientFor(await _fixture.SignInAsync(AdminApiPostgresFixture.HelpdeskEmail));

        (await helpdesk.PutAsync(Release(id, "artifact"), Form(TestArtifacts.UnsignedMsi(Seed("7.74.0-b")), "7.74.0")))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
