using System.Net;
using System.Security.Authentication;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Communication;
using EndpointAgent.Core.Enrollment;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tests.Communication;

/// <summary>
/// What the agent records when it cannot reach the server.
/// </summary>
/// <remarks>
/// <para>
/// Written after an endpoint failed to enrol and the only evidence was
/// <c>"Agent enroll-claim request failed to reach the server: HttpRequestException"</c>.
/// That line is true of an untrusted certificate, a refused connection, a DNS
/// failure and a timeout alike, so it identified nothing -- the actual cause, an
/// untrusted root, sat one level down in the inner exception and was discarded
/// before it reached the log.
/// </para>
/// <para>
/// The second theme matters as much as the first: this client carries enrollment
/// request secrets, device credentials and sealed recovery envelopes in request
/// bodies and headers, so widening what it logs is exactly where a secret would
/// escape. These tests assert the cause is surfaced <em>and</em> that nothing
/// from the request is.
/// </para>
/// </remarks>
public sealed class TransportDiagnosticsTests
{
    /// <summary>A handler that fails every send with a supplied exception.</summary>
    private sealed class ThrowingHandler(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw failure;
    }

    /// <summary>Captures formatted log lines so they can be asserted on.</summary>
    private sealed class CapturingLogger : ILogger<AgentApiClient>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    /// <summary>The shape .NET produces for an untrusted certificate chain.</summary>
    private static HttpRequestException UntrustedRootFailure() =>
        new("The SSL connection could not be established, see inner exception.",
            new AuthenticationException(
                "The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot"));

    private static (AgentApiClient Client, CapturingLogger Logger) Build(Exception failure)
    {
        var logger = new CapturingLogger();
        var http = new HttpClient(new ThrowingHandler(failure))
        {
            BaseAddress = new Uri("https://example.invalid", UriKind.Absolute),
        };

        return (new AgentApiClient(http, logger), logger);
    }

    // ---- the cause reaches the log ----------------------------------------

    /// <summary>
    /// The test this file exists for. A TLS trust failure must be readable as one.
    /// </summary>
    [Fact]
    public async Task An_untrusted_certificate_chain_is_named_in_the_log()
    {
        var (client, logger) = Build(UntrustedRootFailure());

        var result = await client.ClaimEnrollmentAsync(new EnrollmentClaimRequest("s3cr3t-request-secret"));

        result.Status.ShouldBe(AgentApiStatus.TransientFailure, "a transport failure is retryable, not a refusal");

        var line = logger.Lines.ShouldHaveSingleItem();

        line.ShouldContain("enroll-claim");
        line.ShouldContain("HttpRequestException");

        // The part that was previously discarded.
        line.ShouldContain("AuthenticationException");
        line.ShouldContain("UntrustedRoot");
    }

    /// <summary>Distinguishable from the trust failure rather than collapsing onto it.</summary>
    [Fact]
    public async Task A_refused_connection_reads_differently_from_a_trust_failure()
    {
        var (client, logger) = Build(new HttpRequestException(
            "Connection refused.",
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)));

        await client.ClaimEnrollmentAsync(new EnrollmentClaimRequest("secret"));

        var line = logger.Lines.ShouldHaveSingleItem();

        line.ShouldContain("SocketException");
        line.ShouldNotContain("UntrustedRoot");
    }

    /// <summary>A failure with no inner cause still reads cleanly.</summary>
    [Fact]
    public async Task A_bare_failure_logs_its_own_message_without_a_chain_separator()
    {
        var (client, logger) = Build(new HttpRequestException("No such host is known."));

        await client.ClaimEnrollmentAsync(new EnrollmentClaimRequest("secret"));

        var line = logger.Lines.ShouldHaveSingleItem();

        line.ShouldContain("No such host is known.");
        line.ShouldNotContain("->");
    }

    // ---- and nothing else does --------------------------------------------

    /// <summary>
    /// The constraint on widening this log. The claim request secret is the value
    /// that redeems an approved enrollment; it travels in the body of the very call
    /// under test and must not appear in any diagnostic.
    /// </summary>
    [Fact]
    public async Task The_enrollment_request_secret_never_reaches_the_log()
    {
        const string RequestSecret = "kQ7Zx1p9vTn4LmR2yB8sJdH6wC0aE3gU5fO1iN7tX9c=";

        var (client, logger) = Build(UntrustedRootFailure());

        await client.ClaimEnrollmentAsync(new EnrollmentClaimRequest(RequestSecret));

        foreach (var line in logger.Lines)
        {
            line.ShouldNotContain(RequestSecret);
            line.ShouldNotContain("kQ7Zx1p9");
        }
    }

    /// <summary>
    /// A device credential travels in a header on the authenticated calls. Asserted
    /// on heartbeat because that is the call that carries one on every cycle.
    /// </summary>
    [Fact]
    public async Task A_device_credential_never_reaches_the_log()
    {
        const string CredentialSecret = "b7f2e9a4c1d83065aa9e4f7b2c8d1e60";

        var (client, logger) = Build(UntrustedRootFailure());
        var credential = new DeviceCredential(
            Guid.CreateVersion7(), Guid.CreateVersion7().ToString("N"), CredentialSecret, null);

        await client.HeartbeatAsync(
            new HeartbeatRequest("PC-2", "1.4.1", "Windows 11 Pro", DateTimeOffset.UtcNow), credential);

        foreach (var line in logger.Lines)
        {
            line.ShouldNotContain(CredentialSecret);
        }
    }

    /// <summary>
    /// An exception message is bounded, so a pathological or hostile message cannot
    /// flood the log file the agent writes to a fixed-size directory.
    /// </summary>
    [Fact]
    public async Task A_very_long_exception_message_is_truncated()
    {
        var (client, logger) = Build(new HttpRequestException(new string('x', 5000)));

        await client.ClaimEnrollmentAsync(new EnrollmentClaimRequest("secret"));

        var line = logger.Lines.ShouldHaveSingleItem();

        line.Length.ShouldBeLessThan(1000);
        line.ShouldContain("...");
    }

    /// <summary>A self-referencing exception chain terminates rather than hanging.</summary>
    [Fact]
    public async Task A_deep_exception_chain_is_bounded()
    {
        Exception nested = new InvalidOperationException("depth-6");
        for (var i = 5; i >= 1; i--)
        {
            nested = new InvalidOperationException($"depth-{i}", nested);
        }

        var (client, logger) = Build(new HttpRequestException("outer", nested));

        await client.ClaimEnrollmentAsync(new EnrollmentClaimRequest("secret"));

        var line = logger.Lines.ShouldHaveSingleItem();

        // Four levels are recorded; anything deeper is dropped.
        line.ShouldContain("depth-1");
        line.ShouldNotContain("depth-6");
    }
}
