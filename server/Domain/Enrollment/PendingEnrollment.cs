namespace EndpointPlatform.Domain.Enrollment;

/// <summary>
/// Where an enrollment request stands between an agent asking and a credential
/// being handed over.
/// </summary>
public enum PendingEnrollmentStatus
{
    /// <summary>Waiting for an administrator to approve or reject it.</summary>
    Pending = 0,

    /// <summary>An administrator approved it; the agent may now claim its credential.</summary>
    Approved = 1,

    /// <summary>An administrator refused it. No credential is ever issued for this request.</summary>
    Rejected = 2,
}

/// <summary>
/// A machine asking to be managed, before it has any credential.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately NOT a database entity. A pending request is short-lived
/// (15 minutes), carries no authority, and an unauthenticated endpoint creates it —
/// so persisting it in PostgreSQL would let anyone on the internet write rows into
/// the managed estate's own database and would leave expiry as a cleanup job
/// somebody has to remember. Redis expires it automatically and forgets it for free.
/// </para>
/// <para>
/// A pending request grants nothing. It cannot run tasks, read inventory, or reach
/// any authenticated endpoint. Until an administrator approves it, the only thing it
/// can do is be polled for by the agent that created it.
/// </para>
/// </remarks>
/// <param name="MachineIdentifier">
/// SMBIOS system UUID. Not a secret and not an authenticator — it exists so an
/// approved re-enrolment resolves to the SAME device record rather than a duplicate.
/// </param>
/// <param name="Hostname">Reported hostname, shown to the approving administrator.</param>
/// <param name="OperatingSystem">Reported OS description, shown for recognition.</param>
/// <param name="AgentVersion">Reported agent version, shown for recognition.</param>
/// <param name="RequestedAt">When the agent asked.</param>
/// <param name="Status">Current state.</param>
/// <param name="OrganizationId">
/// Set at approval time from the approving administrator's organization. The agent
/// never supplies this: an unauthenticated caller must not be able to choose which
/// tenant it joins.
/// </param>
/// <param name="SealedTokenSecret">
/// Set at approval time. The sealed secret of a single-use enrollment token minted
/// server-side, which the claim step feeds to the existing enrollment path. Never
/// leaves the server and never reaches the agent.
/// </param>
/// <param name="ApprovedBy">Display name of the approving administrator, for audit.</param>
public sealed record PendingEnrollment(
    string MachineIdentifier,
    string Hostname,
    string? OperatingSystem,
    string AgentVersion,
    DateTimeOffset RequestedAt,
    PendingEnrollmentStatus Status,
    Guid? OrganizationId = null,
    string? SealedTokenSecret = null,
    string? ApprovedBy = null)
{
    /// <summary>How long a request survives without a decision.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
}
