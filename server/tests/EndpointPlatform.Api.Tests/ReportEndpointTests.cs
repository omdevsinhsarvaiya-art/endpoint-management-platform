using System.Net;
using System.Text.Json;

namespace EndpointPlatform.Api.Tests;

/// <summary>Phase 15: the consolidated fleet report is gated on device.view and returns all rollups.</summary>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class ReportEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static readonly Uri Summary = new("/admin/v1/reports/summary", UriKind.Relative);

    [Fact]
    public async Task Auditor_can_read_the_fleet_report_and_it_has_all_rollups()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.AuditorEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.GetAsync(Summary);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        foreach (var section in new[] { "devices", "security", "updates", "policies", "tasks" })
        {
            root.TryGetProperty(section, out _).ShouldBeTrue($"report must include '{section}'");
        }

        root.GetProperty("devices").GetProperty("total").GetInt32().ShouldBeGreaterThanOrEqualTo(0);
        root.GetProperty("tasks").GetProperty("succeeded").GetInt32().ShouldBeGreaterThanOrEqualTo(0);
        root.GetProperty("activePackages").GetInt32().ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task An_anonymous_caller_is_rejected()
    {
        using var client = _fixture.CreateClientFor("not-a-session");
        (await client.GetAsync(Summary)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
