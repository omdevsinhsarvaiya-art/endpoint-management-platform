using EndpointPlatform.Domain.Common;
using EndpointPlatform.Domain.Devices;

namespace EndpointPlatform.Domain.Identity;

/// <summary>
/// Where an elevation is in its life.
/// </summary>
/// <remarks>
/// Explicit rather than derived from which timestamps happen to be null. A
/// nullable-timestamp model cannot distinguish "approved but the endpoint has
/// not applied it yet" from "applied", and those are exactly the two cases an
/// operator asks about when something looks wrong.
/// </remarks>
public enum LocalAdminElevationState
{
    /// <summary>Asked for. Confers nothing.</summary>
    Requested = 0,

    /// <summary>
    /// Authorized, with a deadline, but the endpoint has not confirmed it.
    /// </summary>
    /// <remarks>
    /// The window is already running: the deadline is absolute and starts at
    /// approval, not at activation. An endpoint that is slow to collect the task
    /// gets less elevated time, never more — the alternative would let an offline
    /// machine bank its authorization and spend it later.
    /// </remarks>
    Approved = 1,

    /// <summary>The endpoint has confirmed the account is now an administrator.</summary>
    Active = 2,

    /// <summary>Decided against. Terminal.</summary>
    Rejected = 3,

    /// <summary>The deadline passed. Terminal.</summary>
    Expired = 4,

    /// <summary>Ended early by an administrator. Terminal.</summary>
    Revoked = 5,

    /// <summary>
    /// The endpoint could not apply the elevation. Terminal.
    /// </summary>
    /// <remarks>
    /// Reachable only from <see cref="Approved"/> — it means the account never
    /// became an administrator. A failure to <em>remove</em> rights later is not
    /// this: see the class remarks on why that lands on Expired instead.
    /// </remarks>
    Failed = 6,
}

/// <summary>
/// One episode of temporary local administrator rights: who asked, who approved,
/// for which account, and when it stops.
/// </summary>
/// <remarks>
/// <para>
/// <b>This entity records authorization, not enforcement.</b> The distinction is
/// the whole design. When the deadline passes the elevation becomes
/// <see cref="LocalAdminElevationState.Expired"/> whether or not the endpoint
/// managed to remove the rights — because the authorization genuinely ended, and
/// a record that stayed "Active" because a de-elevation failed would be claiming
/// the account is still *permitted* to be an administrator when it is not.
/// </para>
/// <para>
/// Whether the account is still an administrator in fact is answered from what
/// the endpoint reports: <see cref="DeviceLocalUser.IsLocalAdministrator"/>, which
/// the agent already sends with every inventory. An expired elevation whose
/// account still reports as an administrator is drift, and reads exactly like the
/// USB console's Drifted state — decided against reported reality rather than
/// against what the server hoped. That needs no new persistence, so none is
/// added.
/// </para>
/// <para>
/// Expiry is judged from the clock rather than from stored status, so an
/// elevation stops conferring anything the instant its deadline passes,
/// regardless of whether a sweeper has run. The sweeper is bookkeeping.
/// </para>
/// </remarks>
public sealed class LocalAdminElevation : AuditableEntity
{
    /// <summary>
    /// Shortest window that is a decision rather than a misclick.
    /// </summary>
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Longest window the platform will authorize.
    /// </summary>
    /// <remarks>
    /// Shorter than the USB ceiling of 24 hours, deliberately. A read-only USB
    /// grant lets someone copy files off a stick; local administrator rights let
    /// them install software, stop this agent, and edit the machine's security
    /// state. A window long enough to span a working day is long enough for the
    /// reason it was granted to have been forgotten.
    /// </remarks>
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(8);

    private LocalAdminElevation()
    {
        TargetSid = null!;
        TargetUsername = null!;
        Justification = null!;
        RequestedByDisplay = null!;
    }

    private LocalAdminElevation(
        Guid organizationId,
        Guid deviceId,
        string targetSid,
        string targetUsername,
        string justification,
        Guid requestedById,
        string requestedByDisplay,
        DateTimeOffset now)
    {
        OrganizationId = Guard.NotEmpty(organizationId);
        DeviceId = Guard.NotEmpty(deviceId);

        // The SID is the identity. The username is carried alongside for the
        // console and for audit readability, and is never what a decision is
        // matched on: local accounts can be renamed, and a rename must not
        // silently retarget a live elevation.
        //
        // Validated BEFORE the built-in check below, which reads the string:
        // an absent SID must fail as malformed input rather than as a null
        // dereference inside a safety rule.
        TargetSid = Guard.NotNullOrWhiteSpace(targetSid, nameof(targetSid), maxLength: 184);

        // Refused at construction, so no path in the system can produce a record
        // that even proposes elevating the built-in Administrator. It is already
        // an administrator, it is protected from demotion elsewhere, and an
        // "elevation" of it could only ever be a mistake or an attempt to launder
        // one through this feature.
        if (LocalAccountSafetyRules.IsBuiltInAdministrator(TargetSid))
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetSid),
                "The built-in Administrator account cannot be the target of an elevation.");
        }
        TargetUsername = Guard.NotNullOrWhiteSpace(targetUsername, nameof(targetUsername), maxLength: 256);

        Justification = Guard.NotNullOrWhiteSpace(justification, nameof(justification), maxLength: 1000);
        RequestedById = Guard.NotEmpty(requestedById);
        RequestedByDisplay = Guard.NotNullOrWhiteSpace(
            requestedByDisplay, nameof(requestedByDisplay), maxLength: 256);

        State = LocalAdminElevationState.Requested;
        RequestedAt = now;
    }

    /// <summary>Raises a request that still needs a decision.</summary>
    public static LocalAdminElevation Request(
        Guid organizationId,
        Guid deviceId,
        string targetSid,
        string targetUsername,
        string justification,
        Guid requestedById,
        string requestedByDisplay,
        DateTimeOffset now) =>
        new(organizationId, deviceId, targetSid, targetUsername, justification,
            requestedById, requestedByDisplay, now);

    /// <summary>
    /// Raises a request and approves it in one act.
    /// </summary>
    /// <remarks>
    /// For the case where the administrator raising the request is the one
    /// authorized to decide it. Splitting that into two recorded steps would
    /// audit a deliberation that never happened. The two states remain distinct
    /// in the model so a genuine second-person approval can be added later
    /// without reshaping anything.
    /// </remarks>
    public static LocalAdminElevation RequestAndApprove(
        Guid organizationId,
        Guid deviceId,
        string targetSid,
        string targetUsername,
        string justification,
        Guid administratorId,
        string administratorDisplay,
        TimeSpan duration,
        DateTimeOffset now)
    {
        var elevation = Request(
            organizationId, deviceId, targetSid, targetUsername, justification,
            administratorId, administratorDisplay, now);

        elevation.Approve(administratorId, administratorDisplay, duration, now);
        return elevation;
    }

    public Guid OrganizationId { get; private set; }

    public Guid DeviceId { get; private set; }

    /// <summary>The account's Windows SID. The only identity a decision is matched on.</summary>
    public string TargetSid { get; private set; }

    /// <summary>The account's name as reported. Presentation and audit only.</summary>
    public string TargetUsername { get; private set; }

    public LocalAdminElevationState State { get; private set; }

    /// <summary>Why the rights are needed. Required — an unexplained elevation is not auditable.</summary>
    public string Justification { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public Guid RequestedById { get; private set; }

    public string RequestedByDisplay { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public Guid? ApprovedById { get; private set; }

    public string? ApprovedByDisplay { get; private set; }

    /// <summary>When the endpoint confirmed the account had actually been elevated.</summary>
    public DateTimeOffset? ActivatedAt { get; private set; }

    /// <summary>
    /// The absolute instant authorization ends. Null until approved.
    /// </summary>
    /// <remarks>
    /// Chosen once, at approval, and never moved outwards. Extending a window
    /// would make "when does this end" a question with more than one answer;
    /// a longer window is a revoke and a fresh request, which leaves two audit
    /// records instead of one that changed meaning.
    /// </remarks>
    public DateTimeOffset? ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public Guid? RevokedById { get; private set; }

    /// <summary>Why it was rejected or revoked.</summary>
    public string? DecisionNote { get; private set; }

    /// <summary>Why the endpoint could not apply the elevation. Set only with <see cref="LocalAdminElevationState.Failed"/>.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>
    /// True while this elevation actually authorizes administrator rights.
    /// </summary>
    /// <remarks>
    /// Computed from the clock, not from <see cref="State"/> alone. An elevation
    /// whose deadline has passed confers nothing the instant it passes, whether
    /// or not any sweeper has relabelled it — which is what makes a missed sweep
    /// a cosmetic problem rather than a security one.
    /// </remarks>
    public bool IsLive(DateTimeOffset now) =>
        State is LocalAdminElevationState.Approved or LocalAdminElevationState.Active
        && ExpiresAt is { } expiry
        && expiry > now;

    /// <summary>True once nothing further can happen to this record.</summary>
    public bool IsTerminal =>
        State is LocalAdminElevationState.Rejected
            or LocalAdminElevationState.Expired
            or LocalAdminElevationState.Revoked
            or LocalAdminElevationState.Failed;

    public void Approve(Guid approverId, string approverDisplay, TimeSpan duration, DateTimeOffset now)
    {
        if (State != LocalAdminElevationState.Requested)
        {
            throw new InvalidOperationException(
                $"Only a requested elevation can be approved; this one is {State}.");
        }

        if (duration < MinimumDuration || duration > MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                $"An elevation must last between {MinimumDuration.TotalMinutes:0} minutes and "
                + $"{MaximumDuration.TotalHours:0} hours; {duration} was asked for.");
        }

        State = LocalAdminElevationState.Approved;
        ApprovedById = Guard.NotEmpty(approverId);
        ApprovedByDisplay = Guard.NotNullOrWhiteSpace(approverDisplay, nameof(approverDisplay), maxLength: 256);
        ApprovedAt = now;

        // The window starts now, not at activation. See the note on Approved.
        ExpiresAt = now.Add(duration);
    }

    public void Reject(Guid approverId, string approverDisplay, string? note, DateTimeOffset now)
    {
        if (State != LocalAdminElevationState.Requested)
        {
            throw new InvalidOperationException(
                $"Only a requested elevation can be rejected; this one is {State}.");
        }

        State = LocalAdminElevationState.Rejected;
        ApprovedById = Guard.NotEmpty(approverId);
        ApprovedByDisplay = Guard.NotNullOrWhiteSpace(approverDisplay, nameof(approverDisplay), maxLength: 256);
        ApprovedAt = now;
        DecisionNote = Guard.OptionalMaxLength(note, 1000);
    }

    /// <summary>
    /// Records that the endpoint has confirmed the account is now an administrator.
    /// </summary>
    /// <returns>False when the elevation was not in a state to be activated.</returns>
    /// <remarks>
    /// Refuses to activate an elevation whose deadline has already passed. A task
    /// collected late by an endpoint that was offline must not be able to open a
    /// window that has already closed — the report arrives after the fact, and
    /// treating it as an activation would resurrect a lapsed authorization.
    /// </remarks>
    public bool TryActivate(DateTimeOffset now)
    {
        if (State != LocalAdminElevationState.Approved)
        {
            return false;
        }

        if (ExpiresAt is not { } expiry || expiry <= now)
        {
            return false;
        }

        State = LocalAdminElevationState.Active;
        ActivatedAt = now;
        return true;
    }

    /// <summary>
    /// Records that the endpoint could not apply the elevation.
    /// </summary>
    /// <remarks>
    /// Only from <see cref="LocalAdminElevationState.Approved"/> — this means the
    /// account never became an administrator. A failure to <em>remove</em> rights
    /// at the end of a window is a different thing entirely and must not land
    /// here: the authorization still ended, so that case expires, and the
    /// still-elevated account surfaces as drift against what the endpoint reports.
    /// </remarks>
    public bool TryMarkFailed(string reason, DateTimeOffset now)
    {
        if (State != LocalAdminElevationState.Approved)
        {
            return false;
        }

        State = LocalAdminElevationState.Failed;
        FailureReason = Guard.OptionalMaxLength(reason, 1000);
        ExpiresAt = now;
        return true;
    }

    /// <summary>Ends a live elevation early. Returns false when there was nothing to end.</summary>
    /// <remarks>
    /// The deadline moves to now, so a "was this authorized at time T?" question
    /// gives the same answer whether it consults the state or the window. Without
    /// that, a revoked elevation would still look live to any query written
    /// against the deadline.
    /// </remarks>
    public bool TryRevoke(Guid actorId, string actorDisplay, string? note, DateTimeOffset now)
    {
        if (State is not (LocalAdminElevationState.Approved or LocalAdminElevationState.Active))
        {
            return false;
        }

        State = LocalAdminElevationState.Revoked;
        RevokedById = Guard.NotEmpty(actorId);
        RevokedAt = now;
        DecisionNote = Guard.OptionalMaxLength(note, 1000)
            ?? $"Revoked by {Guard.NotNullOrWhiteSpace(actorDisplay, nameof(actorDisplay), maxLength: 256)}.";
        ExpiresAt = now;
        return true;
    }

    /// <summary>
    /// Marks a lapsed elevation Expired. Returns false if it had not lapsed.
    /// </summary>
    /// <remarks>
    /// Bookkeeping only. Authorization ended at the deadline whatever this does,
    /// and it says nothing about whether the endpoint succeeded in removing the
    /// rights — that is answered by what the endpoint reports about the account.
    /// </remarks>
    public bool TryExpire(DateTimeOffset now)
    {
        if (State is not (LocalAdminElevationState.Approved or LocalAdminElevationState.Active))
        {
            return false;
        }

        if (ExpiresAt is not { } expiry || expiry > now)
        {
            return false;
        }

        State = LocalAdminElevationState.Expired;
        return true;
    }

    /// <summary>
    /// Whether a new elevation for this account would collide with an existing one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pure check over a snapshot, used to refuse an obviously-conflicting
    /// request with a clear message rather than a constraint violation.
    /// </para>
    /// <para>
    /// <b>It is not the guarantee.</b> Two concurrent requests can both read a
    /// snapshot with no live elevation and both pass this, so the invariant is
    /// enforced in the database when persistence is introduced: a partial unique
    /// index over (DeviceId, TargetSid) restricted to the non-terminal states,
    /// so at most one row can be live for an account at a time and the loser of a
    /// race fails on insert rather than silently creating a second window.
    /// </para>
    /// </remarks>
    public static bool WouldConflict(
        IEnumerable<LocalAdminElevation> existing,
        Guid deviceId,
        string targetSid,
        DateTimeOffset now) =>
        existing.Any(e =>
            e.DeviceId == deviceId
            && string.Equals(e.TargetSid, targetSid, StringComparison.OrdinalIgnoreCase)
            && (e.IsLive(now) || e.State == LocalAdminElevationState.Requested));
}
