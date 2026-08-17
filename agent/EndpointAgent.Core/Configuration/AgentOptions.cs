using System.ComponentModel.DataAnnotations;

namespace EndpointAgent.Core.Configuration;

/// <summary>Configuration for the Windows endpoint agent.</summary>
/// <remarks>
/// Contains no credential material. The enrollment token is supplied once at
/// install time and the resulting device credential is stored by
/// <c>IDeviceCredentialStore</c> (DPAPI-protected, machine scope) - never in a
/// configuration file, so a readable config never yields a usable identity.
/// </remarks>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>
    /// Base address of the Agent API, e.g. <c>https://endpoint.example.internal:5081</c>.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [Url]
    public string ServerBaseUrl { get; init; } = string.Empty;

    /// <summary>Seconds between heartbeats.</summary>
    [Range(15, 3600)]
    public int HeartbeatIntervalSeconds { get; init; } = 60;

    /// <summary>Per-request HTTP timeout, in seconds.</summary>
    [Range(5, 300)]
    public int RequestTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Allows the agent to trust a server certificate that does not chain to a
    /// trusted root. Development only; the service refuses to start with this
    /// enabled unless the build is a Debug build, because an agent that skips
    /// certificate validation can be trivially man-in-the-middled into accepting
    /// hostile privileged tasks.
    /// </summary>
    public bool AllowUntrustedServerCertificate { get; init; }

    /// <summary>
    /// Directory holding agent state (device credential, cached policy). Defaults to
    /// ProgramData; must be ACL'd to SYSTEM and Administrators only.
    /// </summary>
    public string? StateDirectory { get; init; }
}
