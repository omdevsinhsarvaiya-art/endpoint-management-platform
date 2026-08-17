using System.Net;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// CORS on the Admin API is a security control, not a convenience.
/// </summary>
/// <remarks>
/// The Admin API is credentialed. If it echoed arbitrary origins back with
/// <c>Access-Control-Allow-Credentials</c>, any website an administrator visited
/// could drive their session against it. These tests pin the allow-list behaviour.
/// </remarks>
[Collection(AdminApiCollection.Name)]
public sealed class AdminApiCorsTests(AdminApiFactory factory)
{
    private readonly AdminApiFactory _factory = factory;

    [Fact]
    public async Task The_configured_dashboard_origin_is_allowed()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, new Uri("/", UriKind.Relative));
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(request);

        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed).ShouldBeTrue();
        allowed!.ShouldContain("http://localhost:5173");
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost:5174")]
    [InlineData("null")]
    public async Task An_origin_outside_the_allow_list_receives_no_cors_headers(string origin)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, new Uri("/", UriKind.Relative));
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse(
            $"origin {origin} is not in the allow-list and must not be granted CORS access");
    }

    [Fact]
    public async Task The_wildcard_origin_is_never_returned()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/", UriKind.Relative));
        request.Headers.Add("Origin", "http://localhost:5173");

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        if (response.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed))
        {
            allowed.ShouldNotContain("*", "a wildcard origin combined with credentials is unsafe");
        }
    }
}
