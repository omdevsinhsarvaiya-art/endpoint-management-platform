using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Phase 8: the fleet Windows Update overview endpoint is gated on device.view
/// and returns the expected summary shape.
/// </summary>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class UpdatesEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static readonly Uri Overview = new("/admin/v1/updates/overview", UriKind.Relative);

    [Fact]
    public async Task Auditor_can_read_the_update_overview()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.AuditorEmail);
        using var client = _fixture.CreateClientFor(token);

        var response = await client.GetAsync(Overview);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var summary = doc.RootElement.GetProperty("summary");
        summary.GetProperty("devicesReporting").GetInt32().ShouldBeGreaterThanOrEqualTo(0);
        summary.GetProperty("rebootPending").GetInt32().ShouldBeGreaterThanOrEqualTo(0);
        summary.GetProperty("withFailedUpdates").GetInt32().ShouldBeGreaterThanOrEqualTo(0);
        doc.RootElement.TryGetProperty("devices", out var devices).ShouldBeTrue();
        devices.ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Fact]
    public async Task An_anonymous_caller_is_rejected()
    {
        using var client = _fixture.CreateClientFor(token: "not-a-real-session-token");

        (await client.GetAsync(Overview)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
