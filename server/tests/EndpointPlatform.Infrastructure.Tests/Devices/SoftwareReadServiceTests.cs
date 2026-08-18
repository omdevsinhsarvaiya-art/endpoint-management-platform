using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Tests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Tests.Devices;

[Collection(PostgresCollection.Name)]
public sealed class SoftwareReadServiceTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    [Fact]
    public async Task Titles_aggregate_install_counts_across_devices_and_are_searchable()
    {
        await using var db = _fixture.CreateDbContext();
        var slug = ("sw" + Guid.CreateVersion7().ToString("N")).Substring(0, 20);
        var org = new Organization("SW Org", slug);
        db.Organizations.Add(org);

        var now = DateTimeOffset.UtcNow;
        var token = new Domain.Enrollment.EnrollmentToken(
            org.Id, "t", new string('a', 64), Guid.CreateVersion7(), "a@b", now.AddHours(1), 5);
        db.EnrollmentTokens.Add(token);

        var d1 = Device.Enroll(org.Id, "PC1", "m-" + Guid.CreateVersion7().ToString("N"), "1.0", null, token.Id, now);
        var d2 = Device.Enroll(org.Id, "PC2", "m-" + Guid.CreateVersion7().ToString("N"), "1.0", null, token.Id, now);
        db.Devices.AddRange(d1, d2);

        db.DeviceSoftware.AddRange(
            new DeviceSoftware(d1.Id, "Google Chrome", "120", "Google", null, null, "x64", now),
            new DeviceSoftware(d2.Id, "Google Chrome", "120", "Google", null, null, "x64", now),
            new DeviceSoftware(d1.Id, "7-Zip", "23", "Igor Pavlov", null, null, "x64", now));
        await db.SaveChangesAsync();

        var service = new SoftwareReadService(db);

        var all = await service.ListTitlesAsync(org.Id, 1, 50, null, null, CancellationToken.None);
        all.Items.Single(t => t.Name == "Google Chrome").InstallCount.ShouldBe(2);
        all.Items.Single(t => t.Name == "7-Zip").InstallCount.ShouldBe(1);

        var searched = await service.ListTitlesAsync(org.Id, 1, 50, "chrome", null, CancellationToken.None);
        searched.Items.ShouldHaveSingleItem().Name.ShouldBe("Google Chrome");

        var publishers = await service.ListPublishersAsync(org.Id, CancellationToken.None);
        publishers.ShouldContain("Google");
        publishers.ShouldContain("Igor Pavlov");
    }
}
