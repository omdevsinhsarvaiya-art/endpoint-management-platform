using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Milestone 1.5.0: the fleet software inventory browser — distinct titles with
/// a device count, and the drill-down naming the devices a title is installed on.
/// </summary>
/// <remarks>
/// The counting rule is the substance here. Since per-user installs are
/// collected, one device can hold several rows for one title, so an install count
/// that counted rows would overstate how much of the fleet has an application —
/// which is the number an administrator makes deployment decisions on.
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class SoftwareInventoryEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private sealed record TitleRow(string Name, string? Version, string? Publisher, int InstallCount);

    private sealed record TitlePage(IReadOnlyList<TitleRow> Items, int TotalCount, int Page, int PageSize);

    private sealed record InstallationRow(
        Guid DeviceId, string Hostname, string? DisplayName, string DeviceStatus,
        DateTimeOffset? LastSeenAt, string? InstallationScope, string? InstalledForUser,
        string? Architecture, string? InstallLocation, string? ProductCode, DateTimeOffset CollectedAt);

    private sealed record InstallationPage(
        IReadOnlyList<InstallationRow> Items, int TotalCount, int Page, int PageSize);

    /// <summary>
    /// Seeds one title on two devices: two per-user installs on the first and one
    /// machine-wide install on the second.
    /// </summary>
    private async Task<(string Title, Guid FirstDevice, Guid SecondDevice)> SeedAsync()
    {
        var title = $"Contoso App {Guid.CreateVersion7():N}";

        await using var dbContext = _fixture.CreateDbContext();
        var organizationId = await dbContext.Organizations.Select(o => o.Id).FirstAsync();
        var now = DateTimeOffset.UtcNow;

        var token = new EnrollmentToken(
            organizationId, $"software-inventory-test-{Guid.CreateVersion7():N}",
            Convert.ToHexStringLower(SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
            await dbContext.PlatformUsers.Select(u => u.Id).FirstAsync(), "software-inventory-test",
            now.AddHours(1), 2);
        dbContext.EnrollmentTokens.Add(token);

        var first = Device.Enroll(
            organizationId, $"PC-A-{Guid.CreateVersion7():N}"[..14], $"smbios-{Guid.CreateVersion7()}",
            "1.5.0", "Microsoft Windows 11 Pro", token.Id, now);
        var second = Device.Enroll(
            organizationId, $"PC-B-{Guid.CreateVersion7():N}"[..14], $"smbios-{Guid.CreateVersion7()}",
            "1.5.0", "Microsoft Windows 11 Pro", token.Id, now);
        dbContext.Devices.AddRange(first, second);

        dbContext.DeviceSoftware.AddRange(
            new DeviceSoftware(first.Id, title, "1.0", "Contoso", null, null, null, now, "User", @"CORP\alice"),
            new DeviceSoftware(first.Id, title, "1.0", "Contoso", null, null, null, now, "User", @"CORP\bob"),
            new DeviceSoftware(second.Id, title, "1.0", "Contoso", null, null, "x64", now, "Machine", null));

        await dbContext.SaveChangesAsync();
        return (title, first.Id, second.Id);
    }

    /// <summary>
    /// Two people with the same product on one machine is one device, not two.
    /// </summary>
    [Fact]
    public async Task The_install_count_is_devices_not_rows()
    {
        var (title, _, _) = await SeedAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var page = await client.GetFromJsonAsync<TitlePage>(
            new Uri($"/admin/v1/software?search={Uri.EscapeDataString(title)}", UriKind.Relative));

        var row = page!.Items.Single(t => t.Name == title);
        // Three rows across two devices.
        row.InstallCount.ShouldBe(2);
    }

    /// <summary>
    /// The drill-down reports installations, so a device appears once per user who
    /// has the application — that is the work an administrator still has to do.
    /// </summary>
    [Fact]
    public async Task The_drill_down_lists_every_installation_with_its_scope_and_user()
    {
        var (title, first, second) = await SeedAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var page = await client.GetFromJsonAsync<InstallationPage>(new Uri(
            $"/admin/v1/software/installations?name={Uri.EscapeDataString(title)}&version=1.0&publisher=Contoso",
            UriKind.Relative));

        page!.TotalCount.ShouldBe(3);
        page.Items.Count(i => i.DeviceId == first).ShouldBe(2);
        page.Items.Count(i => i.DeviceId == second).ShouldBe(1);

        page.Items.Where(i => i.DeviceId == first)
            .Select(i => i.InstalledForUser)
            .ShouldBe([@"CORP\alice", @"CORP\bob"], ignoreOrder: true);

        var machineWide = page.Items.Single(i => i.DeviceId == second);
        machineWide.InstallationScope.ShouldBe("Machine");
        machineWide.InstalledForUser.ShouldBeNull();
        machineWide.Architecture.ShouldBe("x64");
    }

    /// <summary>
    /// A title with no recorded version is a distinct title, not a wildcard, so
    /// the drill-down must not return a superset of the row that was clicked.
    /// </summary>
    [Fact]
    public async Task A_version_that_does_not_match_returns_no_installations()
    {
        var (title, _, _) = await SeedAsync();
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var page = await client.GetFromJsonAsync<InstallationPage>(new Uri(
            $"/admin/v1/software/installations?name={Uri.EscapeDataString(title)}&version=9.9&publisher=Contoso",
            UriKind.Relative));

        page!.TotalCount.ShouldBe(0);
        page.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_request_without_a_title_name_is_refused()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.GetAsync(
            new Uri("/admin/v1/software/installations?name=", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The drill-down names machines, so it requires software.view like every
    /// other inventory read — an unauthenticated caller learns nothing.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_caller_cannot_list_installations()
    {
        using var client = _fixture.Factory.CreateClient();

        var response = await client.GetAsync(
            new Uri("/admin/v1/software/installations?name=Anything", UriKind.Relative));

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
