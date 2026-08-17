namespace EndpointPlatform.Contracts.Agent;

/// <summary>
/// Request body for <c>POST /agent/v1/enroll</c>.
/// </summary>
/// <remarks>
/// Carries the enrollment token secret exactly once, over TLS. Every field is
/// validated server-side; none is trusted. The machine identifier is a dedup
/// hint, not authentication (see the Device domain type).
/// </remarks>
public sealed record EnrollRequest(
    string EnrollmentToken,
    string Hostname,
    string MachineIdentifier,
    string AgentVersion,
    string? OperatingSystem);

/// <summary>
/// Response body for a successful enrollment.
/// </summary>
/// <param name="DeviceId">The server-assigned device identity.</param>
/// <param name="CredentialKeyId">Public identifier of the issued credential.</param>
/// <param name="CredentialSecret">
/// The device credential secret, transmitted exactly once, here. The server stores
/// only its hash; the agent must store it via DPAPI immediately and never write it
/// to a log or configuration file.
/// </param>
/// <param name="ReEnrolled">True when an existing device record was updated rather than created.</param>
public sealed record EnrollResponse(
    Guid DeviceId,
    string CredentialKeyId,
    string CredentialSecret,
    bool ReEnrolled);

/// <summary>Request body for <c>POST /agent/v1/heartbeat</c>.</summary>
/// <param name="Hostname">Current hostname; updates the device record.</param>
/// <param name="AgentVersion">Agent build version.</param>
/// <param name="OperatingSystem">OS caption/build, when it changed or on first send.</param>
/// <param name="AgentTimestamp">
/// Agent-local send time; recorded for clock-skew diagnostics, never trusted for
/// ordering — the server's own clock decides <c>last_seen</c>.
/// </param>
public sealed record HeartbeatRequest(
    string Hostname,
    string AgentVersion,
    string? OperatingSystem,
    DateTimeOffset AgentTimestamp);

/// <summary>Response body for a successful heartbeat.</summary>
/// <param name="ServerTime">Server time, letting the agent detect its own skew.</param>
/// <param name="HeartbeatIntervalSeconds">
/// Interval the server wants between heartbeats, so cadence is centrally tunable
/// without redeploying agents.
/// </param>
/// <param name="InventoryRequested">
/// True when the server wants a fresh inventory upload (an administrator asked,
/// or none has ever been received). The agent responds by POSTing to the
/// inventory endpoint; the server never connects to the agent.
/// </param>
public sealed record HeartbeatResponse(
    DateTimeOffset ServerTime,
    int HeartbeatIntervalSeconds,
    bool InventoryRequested = false);
