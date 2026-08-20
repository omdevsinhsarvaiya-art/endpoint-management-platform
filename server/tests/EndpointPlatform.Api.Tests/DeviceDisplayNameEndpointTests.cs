using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// PATCH /admin/v1/devices/{id}/display-name over real HTTP against real
/// PostgreSQL.
/// </summary>
/// <remarks>
/// The domain tests prove <see cref="Device.Rename"/> touches one field. These
/// prove the same thing survives the round trip that actually matters: through
/// authorization, through EF, into the column, and back out of the read model —
/// and that a renamed device is still the same row, with the same id, hostname
/// and machine identifier it enrolled with.
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class DeviceDisplayNameEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static Uri DisplayNameOf(Guid deviceId) =>
        new($"/admin/v1/devices/{deviceId}/display-name", UriKind.Relative);

    private static StringContent Body(string? displayName) => new(
        displayName is null ? """{"displayName":null}""" : $$"""{"displayName":"{{displayName}}"}""",
        System.Text.Encoding.UTF8,
        "application/json");

    /// <summary>Seeds a device directly, so the test does not depend on enrollment.</summary>
    private async Task<(Guid DeviceId, string Hostname, string MachineId)> SeedDeviceAsync(string hostname)
    {
        await using var db = _fixture.CreateDbContext();

        var organizationId = await db.Organizations.Select(o => o.Id).FirstAsync();
        var machineId = $"smbios-{Guid.CreateVersion7()}";

        // A device row needs an admitting token for audit lineage. Nothing here
        // enrolls against it, but the hash still has to be unique -- secret_hash
        // is uniquely indexed, so a shared constant makes every seed after the
        // first collide.
        var token = new EnrollmentToken(
            organizationId,
            $"display-name-test-{Guid.CreateVersion7():N}",
            secretHash: Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
            createdByUserId: await db.PlatformUsers.Select(u => u.Id).FirstAsync(),
            createdByDisplay: "display-name-test",
            expiresAt: DateTimeOffset.UtcNow.AddHours(1),
            maxUses: 1);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            organizationId, hostname, machineId, "1.0.0", "Windows 11 Pro", token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);

        await db.SaveChangesAsync();
        return (device.Id, hostname, machineId);
    }

    private async Task<Device> ReloadAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.Devices.AsNoTracking().SingleAsync(d => d.Id == deviceId);
    }

    // ------------------------------------------------------------ persistence

    [Fact]
    public async Task An_it_admin_can_set_a_display_name_and_it_persists()
    {
        var seeded = await SeedDeviceAsync("LAPTOP-LVCHEQ2H");
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PatchAsync(DisplayNameOf(seeded.DeviceId), Body("TAM0149"));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var reloaded = await ReloadAsync(seeded.DeviceId);
        reloaded.DisplayName.ShouldBe("TAM0149");
        reloaded.Name.ShouldBe("TAM0149");
    }

    [Fact]
    public async Task Renaming_does_not_change_hostname_machine_identifier_or_device_id()
    {
        var seeded = await SeedDeviceAsync("LAPTOP-LVCHEQ2H");
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        await client.PatchAsync(DisplayNameOf(seeded.DeviceId), Body("HR-Laptop-01"));

        var reloaded = await ReloadAsync(seeded.DeviceId);
        reloaded.Id.ShouldBe(seeded.DeviceId);
        reloaded.Hostname.ShouldBe(seeded.Hostname);
        reloaded.MachineIdentifier.ShouldBe(seeded.MachineId);
    }

    [Fact]
    public async Task Renaming_does_not_touch_the_device_agent_credentials()
    {
        var seeded = await SeedDeviceAsync("LAPTOP-CRED");
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        await using (var before = _fixture.CreateDbContext())
        {
            // A rename must not revoke, reissue or otherwise disturb whatever the
            // device authenticates with -- offboarding is the operation that
            // touches credentials, and this is not that.
            var countBefore = await before.AgentCredentials.CountAsync(c => c.DeviceId == seeded.DeviceId);
            await client.PatchAsync(DisplayNameOf(seeded.DeviceId), Body("Accounts-Desk-02"));

            await using var after = _fixture.CreateDbContext();
            var countAfter = await after.AgentCredentials.CountAsync(c => c.DeviceId == seeded.DeviceId);
            countAfter.ShouldBe(countBefore);
        }
    }

    [Fact]
    public async Task Renaming_queues_no_task_for_the_agent()
    {
        var seeded = await SeedDeviceAsync("LAPTOP-NOTASK");
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        await client.PatchAsync(DisplayNameOf(seeded.DeviceId), Body("Rahul-PC"));

        // Nothing about this operation reaches the endpoint. If a task ever showed
        // up here it would mean the console had started renaming Windows.
        await using var db = _fixture.CreateDbContext();
        var tasks = await db.DeviceTasks.CountAsync(t => t.DeviceId == seeded.DeviceId);
        tasks.ShouldBe(0);
    }

    // ----------------------------------------------------------- fallback

    [Fact]
    public async Task Clearing_the_display_name_falls_back_to_the_hostname()
    {
        var seeded = await SeedDeviceAsync("LAPTOP-FALLBACK");
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);
        await client.PatchAsync(DisplayNameOf(seeded.DeviceId), Body("TAM0150"));

        var response = await client.PatchAsync(DisplayNameOf(seeded.DeviceId), Body(null));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var reloaded = await ReloadAsync(seeded.DeviceId);
        reloaded.DisplayName.ShouldBeNull();
        reloaded.Name.ShouldBe("LAPTOP-FALLBACK");
    }

    [Fact]
    public async Task A_blank_display_name_clears_rather_than_storing_an_empty_string()
    {
        var seeded = await SeedDeviceAsync("LAPTOP-BLANK");
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);
        await client.PatchAsync(DisplayNameOf(seeded.DeviceId), Body("TAM0151"));

        await client.PatchAsync(DisplayNameOf(seeded.DeviceId), Body("   "));

        var reloaded = await ReloadAsync(seeded.DeviceId);
        reloaded.DisplayName.ShouldBeNull();
    }

    // ----------------------------------------------------------- read model

    [Fact]
    public async Task The_device_list_carries_both_names()
    {
        var seeded = await SeedDeviceAsync("LAPTOP-LISTED");
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);
        await client.PatchAsync(DisplayNameOf(seeded.DeviceId), Body("TAM0152"));

        var payload = await client.GetStringAsync(
            new Uri("/admin/v1/devices?search=TAM0152", UriKind.Relative));

        // Searching by the label finds it, and the response still identifies the
        // real machine behind it.
        payload.ShouldContain("TAM0152");
        payload.ShouldContain("LAPTOP-LISTED");
    }

    [Fact]
    public async Task The_device_can_still_be_found_by_its_real_hostname_after_renaming()
    {
        var seeded = await SeedDeviceAsync("LAPTOP-FINDME");
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);
        await client.PatchAsync(DisplayNameOf(seeded.DeviceId), Body("TAM0153"));

        var payload = await client.GetStringAsync(
            new Uri("/admin/v1/devices?search=LAPTOP-FINDME", UriKind.Relative));

        payload.ShouldContain("LAPTOP-FINDME");
    }

    [Fact]
    public async Task The_device_detail_carries_both_names()
    {
        var seeded = await SeedDeviceAsync("LAPTOP-DETAIL");
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);
        await client.PatchAsync(DisplayNameOf(seeded.DeviceId), Body("TAM0154"));

        var payload = await client.GetStringAsync(
            new Uri($"/admin/v1/devices/{seeded.DeviceId}", UriKind.Relative));

        payload.ShouldContain("TAM0154");
        payload.ShouldContain("LAPTOP-DETAIL");
    }

    // ----------------------------------------------------------------- rbac

    [Fact]
    public async Task An_unauthenticated_caller_cannot_rename_a_device()
    {
        var seeded = await SeedDeviceAsync("LAPTOP-ANON");
        using var client = _fixture.Factory.CreateClient();

        var response = await client.PatchAsync(DisplayNameOf(seeded.DeviceId), Body("nope"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReloadAsync(seeded.DeviceId)).DisplayName.ShouldBeNull();
    }

    [Theory]
    [InlineData(AdminApiPostgresFixture.AuditorEmail)]
    [InlineData(AdminApiPostgresFixture.HelpdeskEmail)]
    public async Task A_role_without_device_rename_cannot_rename_a_device(string email)
    {
        var seeded = await SeedDeviceAsync($"LAPTOP-{Guid.CreateVersion7():N}"[..24]);
        var token = await _fixture.SignInAsync(email);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PatchAsync(DisplayNameOf(seeded.DeviceId), Body("TAM9999"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReloadAsync(seeded.DeviceId)).DisplayName.ShouldBeNull();
    }

    // ------------------------------------------------------------ validation

    [Fact]
    public async Task An_oversized_display_name_is_rejected_as_a_bad_request()
    {
        var seeded = await SeedDeviceAsync("LAPTOP-TOOLONG");
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PatchAsync(DisplayNameOf(seeded.DeviceId), Body(new string('x', 129)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReloadAsync(seeded.DeviceId)).DisplayName.ShouldBeNull();
    }

    [Fact]
    public async Task Renaming_an_unknown_device_is_a_not_found()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.PatchAsync(DisplayNameOf(Guid.CreateVersion7()), Body("TAM0155"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ----------------------------------------------------------------- audit

    [Fact]
    public async Task Renaming_is_audited_with_the_label_before_and_after()
    {
        var seeded = await SeedDeviceAsync("LAPTOP-AUDITED");
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.ItAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        await client.PatchAsync(DisplayNameOf(seeded.DeviceId), Body("TAM0156"));

        await using var db = _fixture.CreateDbContext();
        // Project the jsonb columns as-is; comparing them in SQL would ask
        // Postgres to treat jsonb as text.
        var entries = await db.AuditLogEntries
            .AsNoTracking()
            .Where(e => e.DeviceId == seeded.DeviceId && e.Action == "device.rename")
            .Select(e => new { e.PreviousState, e.NewState })
            .ToListAsync();

        entries.ShouldNotBeEmpty();
        var entry = entries[0];
        entry.NewState.ShouldNotBeNull();
        entry.NewState!.ShouldContain("TAM0156");
        // The hostname is on both sides, so the trail still identifies the machine.
        entry.NewState.ShouldContain("LAPTOP-AUDITED");
        entry.PreviousState.ShouldNotBeNull();
    }
}
