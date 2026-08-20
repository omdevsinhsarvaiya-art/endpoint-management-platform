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
    bool InventoryRequested = false,
    bool TasksPending = false,
    bool PoliciesPending = false);

/// <summary>
/// An unenrolled machine asking to be managed.
/// </summary>
/// <remarks>
/// Carries no secret and no organization. The agent cannot choose which tenant it
/// joins — that is decided by the administrator who approves it — and it proves
/// possession later rather than presenting a bearer token now.
/// </remarks>
/// <param name="RequestId">
/// SHA-256 of a 256-bit secret the agent generated and kept. The secret itself is
/// NOT sent here; it is revealed only at claim time, which is what makes this
/// proof-of-possession rather than a bearer credential.
/// </param>
/// <param name="MachineIdentifier">
/// SMBIOS system UUID. Not an authenticator — it exists so that approving a machine
/// that was enrolled before resolves to the same device record instead of a duplicate.
/// </param>
public sealed record EnrollmentRequestRequest(
    string RequestId,
    string MachineIdentifier,
    string Hostname,
    string AgentVersion,
    string? OperatingSystem);

/// <summary>Acknowledges that a request is recorded and awaiting a decision.</summary>
/// <param name="Status">Always <c>pending</c> on success; the agent polls the claim endpoint.</param>
/// <param name="PollAfterSeconds">How long the agent should wait before its first claim attempt.</param>
public sealed record EnrollmentRequestResponse(string Status, int PollAfterSeconds);

/// <summary>
/// The agent proving possession of the secret behind its request id.
/// </summary>
/// <param name="RequestSecret">
/// The raw 256-bit secret. Sent only over HTTPS, never logged, never persisted
/// server-side, and the pending request is consumed atomically when it is accepted.
/// </param>
public sealed record EnrollmentClaimRequest(string RequestSecret);

/// <summary>
/// The outcome of a claim. A credential is present only when <paramref name="Status"/>
/// is <c>approved</c>; every other status carries nulls so a caller cannot mistake a
/// refusal for an issuance.
/// </summary>
/// <param name="Status">
/// <c>approved</c>, <c>pending</c> (keep waiting), or <c>rejected</c> (stop asking).
/// </param>
public sealed record EnrollmentClaimResponse(
    string Status,
    Guid? DeviceId,
    string? CredentialKeyId,
    string? CredentialSecret,
    bool ReEnrolled,
    int PollAfterSeconds);
