namespace EndpointAgent.Core.Enrollment;

/// <summary>
/// Where the agent is in the enrollment protocol.
/// </summary>
/// <remarks>
/// Deliberately a small explicit enum rather than a state-machine framework. There
/// are six states and the transitions are linear; anything heavier would obscure the
/// one property that actually matters, which is that the machine is restart-safe.
/// </remarks>
public enum EnrollmentState
{
    /// <summary>No credential and no request. The starting point after a fresh install.</summary>
    Unenrolled = 0,

    /// <summary>A request has been accepted by the server and is awaiting a decision.</summary>
    Pending = 1,

    /// <summary>An administrator refused this machine. The agent stops asking with this request.</summary>
    Rejected = 2,

    /// <summary>A credential is held and normal authenticated operation applies.</summary>
    Enrolled = 3,
}

/// <summary>
/// The minimum the agent must remember to resume an enrollment it already started.
/// </summary>
/// <remarks>
/// <para>
/// This exists because a Windows Service can restart, and Windows can reboot, while a
/// request sits waiting for an administrator. Without persisting it the agent would
/// generate a fresh secret on every start and orphan the request an administrator is
/// looking at — the pending list would fill with duplicates of one machine and
/// approving any of them would race the agent's current request.
/// </para>
/// <para>
/// <b><see cref="RequestSecret"/> is sensitive.</b> It is the proof of possession that
/// redeems an approved enrollment, so it is stored only through the DPAPI-protected
/// store, never in configuration, never in Program Files, never in a log, and never
/// on a command line.
/// </para>
/// </remarks>
/// <param name="RequestSecret">
/// The raw 256-bit secret. Only its SHA-256 was ever sent to the server; this value
/// is revealed exactly once, at claim time.
/// </param>
/// <param name="RequestId">
/// SHA-256 of <paramref name="RequestSecret"/>. Not a credential — it is what the
/// server stores and what identifies the request. Kept so a resumed agent does not
/// have to recompute it, and so logs can name a request without naming its secret.
/// </param>
/// <param name="ServerBaseUrl">
/// The server this request was made to. If configuration is later repointed at a
/// different server, the pending request belongs to the old one and must be
/// abandoned rather than claimed against a server that never saw it.
/// </param>
/// <param name="MachineIdentifier">
/// The identity the request was made under. A mismatch means the state file was
/// copied from another machine, which must not be honoured.
/// </param>
/// <param name="RequestedAt">When the request was submitted, for expiry reasoning.</param>
public sealed record PendingEnrollmentState(
    string RequestSecret,
    string RequestId,
    string ServerBaseUrl,
    string MachineIdentifier,
    DateTimeOffset RequestedAt);
