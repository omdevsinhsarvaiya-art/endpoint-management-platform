using System.Net.Http.Json;
using System.Security.Cryptography;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// The per-device software inventory the device page reads.
/// </summary>
/// <remarks>
/// <para>
/// Written after a production investigation in which OMDEVSINH-TECHS reported
/// 26 applications while the machine actually had 33. The cause was an agent
/// binary that predated per-user discovery, not a defect in this path — but
/// tracing it showed that this endpoint dropped scope, user and product code on
/// the floor, so even a correct agent's per-user data could not have reached the
/// device page. These tests pin the whole row through.
/// </para>
/// <para>
/// There is deliberately no cap here: the count an operator reads must be the
/// number of rows the device reported, and any limit would silently make an
/// incomplete inventory look complete. That is exactly the confusion the
/// investigation had to unpick.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class DeviceSoftwareInventoryTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private sealed record SoftwareItem(
        string Name, string? Version, string? Publisher, string? InstallDate, string? Architecture,
        string? InstallationScope, string? InstalledForUser, string? ProductCode);

    private sealed record DeviceDetail(Guid Id, string Hostname, IReadOnlyList<SoftwareItem> Software);

    private const string PythonCode = "{97B6DE30-6082-48D1-9BB4-9F43296531A4}";

    /// <summary>Seeds a device holding the shapes a real machine reports.</summary>
    private async Task<Guid> SeedAsync(int fillerCount = 0)
    {
        await using var db = _fixture.CreateDbContext();
        var organizationId = await db.Organizations.Select(o => o.Id).FirstAsync();
        var now = DateTimeOffset.UtcNow;

        var token = new EnrollmentToken(
            organizationId, $"inv-{Guid.CreateVersion7():N}",
            Convert.ToHexStringLower(SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
            await db.PlatformUsers.Select(u => u.Id).FirstAsync(), "inventory-tests", now.AddHours(1), 1);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            organizationId, $"INV-{Guid.CreateVersion7():N}"[..12], $"smbios-{Guid.CreateVersion7()}",
            "1.5.0", "Microsoft Windows 11 Pro", token.Id, now);
        db.Devices.Add(device);

        void Add(string name, string? version, string? publisher, string? view,
            string scope, string? user, string? code) =>
            db.DeviceSoftware.Add(new DeviceSoftware(
                device.Id, name, version, publisher, null, null, view, now, scope, user, code));

        // 1. Machine-wide x64.
        Add("WireGuard", "1.1", "WireGuard LLC", "x64", "Machine", null, null);
        // 2. Machine-wide x86 (a 64-bit product under WOW6432Node - Chrome's shape).
        Add("Google Chrome", "152.0.7977.75", "Google LLC", "x86", "Machine", null, null);
        // 3. Per-user, the class that was invisible in production.
        Add("Zoom Workplace", "7.1.5 (43453)", "Zoom Communications, Inc.", null,
            "User", @"OMDEVSINH-TECHS\Techsara", null);
        // 4. The same application for a second user: two installs, not a duplicate.
        Add("Zoom Workplace", "7.1.5 (43453)", "Zoom Communications, Inc.", null,
            "User", @"OMDEVSINH-TECHS\Other", null);
        // 5. Per-user carrying an MSI product code.
        Add("Python 3.14.7 (64-bit)", "3.14.7150.0", "Python Software Foundation", null,
            "User", @"OMDEVSINH-TECHS\Techsara", PythonCode);
        // 6. Missing publisher, and 7. missing version - both real on this fleet.
        Add("Microsoft Windows Application Compatibility Fix Database", null, null, "x64",
            "Machine", null, null);

        for (var i = 0; i < fillerCount; i++)
        {
            Add($"Filler {i:D3}", "1.0.0", "Other", "x64", "Machine", null, null);
        }

        await db.SaveChangesAsync();
        return device.Id;
    }

    private async Task<DeviceDetail> GetAsync(Guid deviceId)
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        return (await client.GetFromJsonAsync<DeviceDetail>(
            new Uri($"/admin/v1/devices/{deviceId}", UriKind.Relative)))!;
    }

    /// <summary>
    /// The regression this file exists for: the endpoint used to project only
    /// name, version, publisher, install date and architecture, so per-user
    /// attribution could never reach the console however correct the agent was.
    /// </summary>
    [Fact]
    public async Task Scope_user_and_product_code_reach_the_device_page()
    {
        var deviceId = await SeedAsync();

        var detail = await GetAsync(deviceId);

        var zoom = detail.Software.First(s => s.Name == "Zoom Workplace");
        zoom.InstallationScope.ShouldBe("User");
        zoom.InstalledForUser.ShouldNotBeNullOrWhiteSpace();

        var python = detail.Software.Single(s => s.Name.StartsWith("Python", StringComparison.Ordinal));
        python.ProductCode.ShouldBe(PythonCode);

        var wireguard = detail.Software.Single(s => s.Name == "WireGuard");
        wireguard.InstallationScope.ShouldBe("Machine");
        // A machine-wide install belongs to no single account.
        wireguard.InstalledForUser.ShouldBeNull();
    }

    /// <summary>
    /// One application installed for two people is two rows. Collapsing them
    /// would report a machine as clean while the application is still there for
    /// somebody.
    /// </summary>
    [Fact]
    public async Task The_same_application_for_two_users_is_two_distinct_rows()
    {
        var deviceId = await SeedAsync();

        var detail = await GetAsync(deviceId);

        var zoom = detail.Software.Where(s => s.Name == "Zoom Workplace").ToList();
        zoom.Count.ShouldBe(2);
        zoom.Select(z => z.InstalledForUser).Distinct().Count().ShouldBe(2);

        // The dashboard keys rows by name + version + user for this reason; name
        // and version alone would collide on exactly these two.
        zoom.Select(z => $"{z.Name}|{z.Version}|{z.InstalledForUser}")
            .Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task Machine_wide_x64_and_x86_entries_are_both_reported()
    {
        var deviceId = await SeedAsync();

        var detail = await GetAsync(deviceId);

        detail.Software.Single(s => s.Name == "WireGuard").Architecture.ShouldBe("x64");
        // Chrome is 64-bit but registers under WOW6432Node, which is why this
        // field is reported as a registry view and not called an architecture.
        detail.Software.Single(s => s.Name == "Google Chrome").Architecture.ShouldBe("x86");
    }

    /// <summary>An absent optional field costs that field, never the row.</summary>
    [Fact]
    public async Task An_entry_missing_a_publisher_or_version_is_still_reported()
    {
        var deviceId = await SeedAsync();

        var detail = await GetAsync(deviceId);

        var oddity = detail.Software.Single(
            s => s.Name == "Microsoft Windows Application Compatibility Fix Database");

        oddity.Version.ShouldBeNull();
        oddity.Publisher.ShouldBeNull();
    }

    /// <summary>
    /// No cap, at any size. A limit here would make an incomplete inventory look
    /// complete, which is the precise failure mode this investigation had to
    /// distinguish from a genuinely short list.
    /// </summary>
    [Fact]
    public async Task Every_reported_application_is_returned_with_no_hidden_limit()
    {
        var deviceId = await SeedAsync(fillerCount: 120);

        var detail = await GetAsync(deviceId);

        await using var db = _fixture.CreateDbContext();
        var persisted = await db.DeviceSoftware.CountAsync(s => s.DeviceId == deviceId);

        persisted.ShouldBe(126);
        detail.Software.Count.ShouldBe(persisted);
    }

    /// <summary>
    /// An agent older than 1.5.0 sends no scope at all. Those rows must still be
    /// served -- most of the fleet is on 1.1.x -- and must read as unknown rather
    /// than being defaulted to "Machine", which would assert something the
    /// platform never determined.
    /// </summary>
    [Fact]
    public async Task Rows_from_an_older_agent_survive_with_a_null_scope()
    {
        await using (var db = _fixture.CreateDbContext())
        {
            var organizationId = await db.Organizations.Select(o => o.Id).FirstAsync();
            var now = DateTimeOffset.UtcNow;

            var token = new EnrollmentToken(
                organizationId, $"legacy-{Guid.CreateVersion7():N}",
                Convert.ToHexStringLower(SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
                await db.PlatformUsers.Select(u => u.Id).FirstAsync(), "inventory-tests",
                now.AddHours(1), 1);
            db.EnrollmentTokens.Add(token);

            var legacy = Device.Enroll(
                organizationId, $"OLD-{Guid.CreateVersion7():N}"[..12], $"smbios-{Guid.CreateVersion7()}",
                "1.1.4", "Microsoft Windows 11 Pro", token.Id, now);
            db.Devices.Add(legacy);

            // Exactly what a pre-1.5.0 agent produces: no scope, no user, no code.
            db.DeviceSoftware.Add(new DeviceSoftware(
                legacy.Id, "Node.js", "24.19.0", "Node.js Foundation", null, null, "x64", now));

            await db.SaveChangesAsync();

            var detail = await GetAsync(legacy.Id);
            var node = detail.Software.Single();

            node.Name.ShouldBe("Node.js");
            node.InstallationScope.ShouldBeNull();
            node.InstalledForUser.ShouldBeNull();
            node.ProductCode.ShouldBeNull();
        }
    }
}
