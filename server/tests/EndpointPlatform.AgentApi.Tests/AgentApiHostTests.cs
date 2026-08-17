using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Common;
using EndpointPlatform.Infrastructure.Hosting;

namespace EndpointPlatform.AgentApi.Tests;

public sealed class AgentApiHostTests(AgentApiFactory factory) : IClassFixture<AgentApiFactory>
{
    private readonly AgentApiFactory _factory = factory;

    [Fact]
    public async Task Liveness_reports_healthy()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Root_identifies_the_service_as_the_agent_api()
    {
        using var client = _factory.CreateClient();

        var info = await client.GetFromJsonAsync<ServiceInfoResponse>(new Uri("/", UriKind.Relative));

        info.ShouldNotBeNull();
        info.Service.ShouldBe("agent-api");
    }

    [Fact]
    public async Task Security_headers_are_applied()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        response.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
        response.Headers.GetValues("X-Frame-Options").ShouldContain("DENY");
    }

    [Fact]
    public async Task A_correlation_id_is_returned()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        response.Headers.TryGetValues(CorrelationId.HeaderName, out _).ShouldBeTrue();
    }

    /// <summary>
    /// CORS must not be enabled on the Agent API. Agents are Windows services making
    /// server-to-server calls; a browser has no legitimate reason to reach this host,
    /// and enabling CORS would only widen the attack surface.
    /// </summary>
    [Fact]
    public async Task Cors_is_not_enabled()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/", UriKind.Relative));
        request.Headers.Add("Origin", "http://localhost:5173");

        var response = await client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse(
            "the Agent API is machine-to-machine and must not grant browser origins access");
    }

    [Fact]
    public void The_agent_protocol_version_is_pinned()
    {
        // Bumping this is a wire-compatibility decision that must be deliberate:
        // every deployed agent sends this value and the server validates it.
        AgentProtocol.Version.ShouldBe(1);
        AgentProtocol.RoutePrefix.ShouldBe("/agent/v1");
    }

    /// <summary>
    /// Phase 0 defines no agent endpoints yet. Asserting that they are absent keeps
    /// the phase boundary honest: enrollment and heartbeat arrive in Phase 1, and
    /// this test will be updated then to assert they require authentication.
    /// </summary>
    [Theory]
    [InlineData(AgentProtocol.RoutePrefix + AgentProtocol.Routes.Enroll)]
    [InlineData(AgentProtocol.RoutePrefix + AgentProtocol.Routes.Heartbeat)]
    [InlineData(AgentProtocol.RoutePrefix + AgentProtocol.Routes.Inventory)]
    public async Task No_agent_endpoint_is_implemented_in_phase_0(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync(new Uri(path, UriKind.Relative), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
