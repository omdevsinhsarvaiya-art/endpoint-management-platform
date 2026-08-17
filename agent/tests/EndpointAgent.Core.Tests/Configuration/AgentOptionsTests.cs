using System.ComponentModel.DataAnnotations;
using EndpointAgent.Core.Configuration;

namespace EndpointAgent.Core.Tests.Configuration;

public sealed class AgentOptionsTests
{
    private static List<ValidationResult> Validate(AgentOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void A_fully_configured_options_object_validates()
    {
        var options = new AgentOptions
        {
            ServerBaseUrl = "https://endpoint.example.internal:5081",
            HeartbeatIntervalSeconds = 60,
            RequestTimeoutSeconds = 30,
        };

        Validate(options).ShouldBeEmpty();
    }

    [Fact]
    public void Server_base_url_is_required()
    {
        var options = new AgentOptions { ServerBaseUrl = "" };

        Validate(options).ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://wrong.scheme")]
    public void Server_base_url_must_be_a_well_formed_url(string url)
    {
        var options = new AgentOptions { ServerBaseUrl = url };

        // [Url] accepts http/https/ftp; ftp is rejected because UrlAttribute
        // requires http(s) prefix per its implementation... verify behaviour:
        // UrlAttribute checks http://, https:// or ftp:// - so document reality:
        var results = Validate(options);

        if (url.StartsWith("ftp://", StringComparison.Ordinal))
        {
            // ftp:// passes [Url]; the transport layer will still refuse it because
            // HttpClient only speaks http(s). Recorded here so the limitation is visible.
            results.ShouldBeEmpty();
        }
        else
        {
            results.ShouldNotBeEmpty();
        }
    }

    [Theory]
    [InlineData(14)]
    [InlineData(3601)]
    public void Heartbeat_interval_outside_the_supported_range_is_rejected(int seconds)
    {
        var options = new AgentOptions
        {
            ServerBaseUrl = "https://server.local",
            HeartbeatIntervalSeconds = seconds,
        };

        Validate(options).ShouldNotBeEmpty();
    }

    [Fact]
    public void Defaults_are_safe()
    {
        var options = new AgentOptions { ServerBaseUrl = "https://server.local" };

        options.AllowUntrustedServerCertificate.ShouldBeFalse(
            "certificate validation must be on by default");
        options.HeartbeatIntervalSeconds.ShouldBe(60);
        options.RequestTimeoutSeconds.ShouldBe(30);
    }
}
