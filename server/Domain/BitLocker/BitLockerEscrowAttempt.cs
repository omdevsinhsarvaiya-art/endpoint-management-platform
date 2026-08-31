using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.BitLocker;

/// <summary>Where automatic escrow has got to for one protector.</summary>
public enum BitLockerEscrowAttemptState
{
    /// <summary>Known about, not yet escrowed, and still eligible to try.</summary>
    Pending = 0,

    /// <summary>A sealed password is filed for this protector.</summary>
    Escrowed = 1,

    /// <summary>The last attempt failed and another is scheduled.</summary>
    Failed = 2,

    /// <summary>
    /// Every attempt in the backoff schedule failed. No further automatic attempt
    /// happens until an administrator resets it or the protector changes.
    /// </summary>
    RetryExhausted = 3,
}

/// <summary>Why an automatic escrow attempt failed, coarsely.</summary>
/// <remarks>
/// Categories, never messages. An error string built from an exception is exactly
/// the kind of place a credential leaks into an append-only audit trail, so the
/// failure surface is a closed set that cannot carry a value.
/// </remarks>
public enum BitLockerEscrowFailureCategory
{
    None = 0,

    /// <summary>Windows refused to return the password (elevation, policy, state).</summary>
    WindowsRefused = 1,

    /// <summary>Windows returned something that is not a valid recovery password.</summary>
    MalformedPassword = 2,

    /// <summary>The sealing key did not match the pinned fingerprint.</summary>
    FingerprintMismatch = 3,

    /// <summary>This device has no pinned fingerprint; it must re-enroll first.</summary>
    NotEligible = 4,

    /// <summary>Sealing failed on the endpoint.</summary>
    SealingFailed = 5,

    /// <summary>The upload could not be completed.</summary>
    UploadFailed = 6,

    /// <summary>The protector vanished between detection and retrieval.</summary>
    ProtectorGone = 7,
}

/// <summary>
/// Retry state for automatic escrow of one protector.
/// </summary>
/// <remarks>
/// <para>
/// A separate table rather than reuse of <c>DeviceTask</c>, which was considered
/// first. Tasks are server-issued, one-shot, and carry no attempt count or next-run
/// time; this is agent-initiated, recurring, and is defined by exactly those two
/// fields. Modelling it as a task would have meant adding retry columns to the task
/// framework for the benefit of a single task type.
/// </para>
/// <para>
/// The schedule lives here, on the server, rather than on the endpoint. An agent
/// that restarts -- or is restarted deliberately -- must not get a fresh budget of
/// attempts, or a machine whose Windows refuses the call would hammer both Windows
/// and the API forever.
/// </para>
/// <para>
/// This row records only that an attempt happened and how it ended. It has no
/// field a password could occupy.
/// </para>
/// </remarks>
public sealed class BitLockerEscrowAttempt : Entity
{
    /// <summary>
    /// Delay before each subsequent attempt: 1m, 5m, 15m, 1h, 6h.
    /// </summary>
    /// <remarks>
    /// Front-loaded because the common failure is transient -- a volume still
    /// converting, a service not yet up after boot -- and back-loaded because the
    /// stubborn failure is a policy or elevation problem that will not fix itself
    /// within the hour.
    /// </remarks>
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
    ];

    /// <summary>Attempts before automatic retrying stops.</summary>
    public static readonly int MaxAttempts = Backoff.Length;

    private BitLockerEscrowAttempt()
    {
        VolumeDeviceIdentifier = null!;
        KeyProtectorId = null!;
    }

    public BitLockerEscrowAttempt(
        Guid organizationId,
        Guid deviceId,
        string volumeDeviceIdentifier,
        string keyProtectorId,
        DateTimeOffset now)
    {
        OrganizationId = Guard.NotEmpty(organizationId, nameof(organizationId));
        DeviceId = Guard.NotEmpty(deviceId, nameof(deviceId));
        VolumeDeviceIdentifier = Guard.NotNullOrWhiteSpace(
            volumeDeviceIdentifier, nameof(volumeDeviceIdentifier), maxLength: 256);
        KeyProtectorId = Guard.NotNullOrWhiteSpace(keyProtectorId, nameof(keyProtectorId), maxLength: 64);

        State = BitLockerEscrowAttemptState.Pending;
        NextAttemptAt = now;
        FirstSeenAt = now;
    }

    public Guid OrganizationId { get; private set; }

    public Guid DeviceId { get; private set; }

    public string VolumeDeviceIdentifier { get; private set; }

    public string KeyProtectorId { get; private set; }

    public BitLockerEscrowAttemptState State { get; private set; }

    /// <summary>How many attempts have failed. Reset only by an administrator.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>
    /// When the next attempt becomes due. Null once escrowed or exhausted -- both
    /// are terminal without intervention.
    /// </summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    public BitLockerEscrowFailureCategory LastFailure { get; private set; }

    public DateTimeOffset FirstSeenAt { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    public DateTimeOffset? EscrowedAt { get; private set; }

    public DateTimeOffset? ResetAt { get; private set; }

    public Guid? ResetByUserId { get; private set; }

    /// <summary>Whether an automatic attempt is due at <paramref name="now"/>.</summary>
    public bool IsDue(DateTimeOffset now) =>
        State is BitLockerEscrowAttemptState.Pending or BitLockerEscrowAttemptState.Failed
        && NextAttemptAt is { } due
        && due <= now;

    public void RecordSuccess(DateTimeOffset now)
    {
        State = BitLockerEscrowAttemptState.Escrowed;
        LastAttemptAt = now;
        EscrowedAt = now;
        NextAttemptAt = null;
        LastFailure = BitLockerEscrowFailureCategory.None;
    }

    /// <summary>
    /// Records a failed attempt and schedules the next, or gives up.
    /// </summary>
    public void RecordFailure(BitLockerEscrowFailureCategory category, DateTimeOffset now)
    {
        AttemptCount++;
        LastAttemptAt = now;
        LastFailure = category;

        if (AttemptCount >= MaxAttempts)
        {
            State = BitLockerEscrowAttemptState.RetryExhausted;
            NextAttemptAt = null;
            return;
        }

        State = BitLockerEscrowAttemptState.Failed;
        NextAttemptAt = now + Backoff[AttemptCount - 1];
    }

    /// <summary>
    /// Clears the failure history so automatic escrow can try again.
    /// </summary>
    /// <remarks>
    /// Deliberately an administrator action rather than anything time-based. A
    /// protector that exhausted its attempts did so because something on the
    /// machine needs attention; retrying it on a timer would bury that signal
    /// instead of surfacing it.
    /// </remarks>
    public void Reset(Guid actorId, DateTimeOffset now)
    {
        AttemptCount = 0;
        State = BitLockerEscrowAttemptState.Pending;
        NextAttemptAt = now;
        LastFailure = BitLockerEscrowFailureCategory.None;
        ResetAt = now;
        ResetByUserId = Guard.NotEmpty(actorId, nameof(actorId));
    }
}
