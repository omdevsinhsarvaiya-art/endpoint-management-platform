using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Peripherals;

/// <summary>Where a request for USB storage access came from.</summary>
/// <remarks>
/// Only <see cref="Administrator"/> can currently be created. The field exists
/// so that an endpoint-initiated flow — where the logged-in user asks from the
/// machine itself — is an additive change rather than a schema migration and a
/// rewrite of every audit query. It is deliberately not implemented yet: the
/// agent is a Session 0 service with no user interface, so accepting requests
/// from the endpoint means shipping a user-session component, and a local
/// listener that could approve its own request would be a hole, not a feature.
/// </remarks>
public enum UsbAccessRequestSource
{
    /// <summary>Raised in the console by an administrator, on a user's behalf.</summary>
    Administrator = 0,

    /// <summary>Reserved: raised from the endpoint by the logged-in user.</summary>
    Endpoint = 1,
}

public enum UsbAccessRequestStatus
{
    /// <summary>Awaiting an administrator decision. Confers no access.</summary>
    Pending = 0,

    /// <summary>Decided in favour. Access is live until <see cref="UsbAccessRequest.ExpiresAt"/>.</summary>
    Approved = 1,

    /// <summary>Decided against. Terminal.</summary>
    Rejected = 2,

    /// <summary>The grant reached its deadline. Terminal.</summary>
    Expired = 3,

    /// <summary>An administrator ended the grant before its deadline. Terminal.</summary>
    Revoked = 4,
}

/// <summary>
/// One episode of temporary access to a USB storage device: who asked, who
/// decided, why, and when it stops.
/// </summary>
/// <remarks>
/// <para>
/// The row is the durable record of the decision, kept after the grant lapses so
/// "who let this stick onto that machine, and when" is answerable months later.
/// Every request names the access level it is for — read-only, or full
/// read/write — and that level is fixed at creation. A grant cannot be widened
/// after the fact: an administrator wanting to move a device from read-only to
/// read/write revokes and re-grants, which leaves two audit rows recording two
/// distinct decisions rather than one row that quietly changed meaning.
/// </para>
/// <para>
/// Expiry is an absolute instant chosen at approval, not a duration counted from
/// first use. A grant cannot be extended — the administrator issues a new one,
/// which leaves two audit rows instead of one mutable one.
/// </para>
/// </remarks>
public sealed class UsbAccessRequest : AuditableEntity
{
    /// <summary>Longest grant the platform will issue, whatever is asked for.</summary>
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(24);

    /// <summary>Shortest grant that is useful rather than a misclick.</summary>
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromMinutes(5);

    private UsbAccessRequest()
    {
        InstanceId = null!;
        Justification = null!;
    }

    private UsbAccessRequest(
        Guid organizationId,
        Guid deviceId,
        Guid usbDeviceId,
        string instanceId,
        UsbAccessRequestSource source,
        UsbStoragePolicy grantedPolicy,
        string justification,
        DateTimeOffset now)
    {
        if (grantedPolicy == UsbStoragePolicy.Restricted || !Enum.IsDefined(grantedPolicy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(grantedPolicy),
                "A request must be for ReadOnly or Enabled; Restricted is the absence of a grant.");
        }

        GrantedPolicy = grantedPolicy;
        OrganizationId = Guard.NotEmpty(organizationId);
        DeviceId = Guard.NotEmpty(deviceId);
        UsbDeviceId = Guard.NotEmpty(usbDeviceId);
        InstanceId = Guard.NotNullOrWhiteSpace(instanceId, nameof(instanceId), maxLength: 512);
        Source = source;
        Justification = Guard.NotNullOrWhiteSpace(justification, nameof(justification), maxLength: 1000);
        Status = UsbAccessRequestStatus.Pending;
        RequestedAt = now;
    }

    /// <summary>
    /// Creates an already-approved grant. The administrator raising it in the
    /// console <em>is</em> the approver, so splitting it into request-then-approve
    /// would record a decision that never actually happened.
    /// </summary>
    public static UsbAccessRequest GrantByAdministrator(
        Guid organizationId,
        Guid deviceId,
        Guid usbDeviceId,
        string instanceId,
        UsbStoragePolicy grantedPolicy,
        string justification,
        Guid approverId,
        string approverDisplay,
        TimeSpan duration,
        DateTimeOffset now)
    {
        var request = new UsbAccessRequest(
            organizationId, deviceId, usbDeviceId, instanceId,
            UsbAccessRequestSource.Administrator, grantedPolicy, justification, now);

        request.Approve(approverId, approverDisplay, duration, now);
        return request;
    }

    public Guid OrganizationId { get; private set; }

    public Guid DeviceId { get; private set; }

    public Guid UsbDeviceId { get; private set; }

    /// <summary>
    /// Copied from the USB device at request time. Denormalised on purpose: the
    /// record must stay readable even if the device row is later pruned, and the
    /// audit answer is "this exact hardware", not "some row id".
    /// </summary>
    public string InstanceId { get; private set; }

    public UsbAccessRequestSource Source { get; private set; }

    /// <summary>
    /// The access level this request is for. Never
    /// <see cref="UsbStoragePolicy.Restricted"/>.
    /// </summary>
    /// <remarks>
    /// Stored on the request rather than read from the device, because the
    /// device row carries only its current state. Months later the audit
    /// question is "what was this person actually given", and only the request
    /// row can still answer it.
    /// </remarks>
    public UsbStoragePolicy GrantedPolicy { get; private set; }

    public UsbAccessRequestStatus Status { get; private set; }

    /// <summary>Why access was needed. Required — an unexplained grant is not auditable.</summary>
    public string Justification { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public Guid? DecidedById { get; private set; }

    public string? DecidedByDisplay { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>When the grant stops. Null unless the request was approved.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>Why a grant was revoked or a request rejected.</summary>
    public string? DecisionNote { get; private set; }

    /// <summary>True while this grant actually confers access.</summary>
    public bool IsLive(DateTimeOffset now) =>
        Status == UsbAccessRequestStatus.Approved && ExpiresAt is { } expiry && expiry > now;

    public void Approve(Guid approverId, string approverDisplay, TimeSpan duration, DateTimeOffset now)
    {
        if (Status != UsbAccessRequestStatus.Pending)
        {
            throw new InvalidOperationException($"Only a pending request can be approved; this one is {Status}.");
        }

        if (duration < MinimumDuration || duration > MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                $"A grant must last between {MinimumDuration.TotalMinutes:0} minutes and " +
                $"{MaximumDuration.TotalHours:0} hours; {duration} was asked for.");
        }

        Status = UsbAccessRequestStatus.Approved;
        DecidedById = Guard.NotEmpty(approverId);
        DecidedByDisplay = Guard.NotNullOrWhiteSpace(approverDisplay, nameof(approverDisplay), maxLength: 256);
        DecidedAt = now;
        ExpiresAt = now.Add(duration);
    }

    public void Reject(Guid approverId, string approverDisplay, string? note, DateTimeOffset now)
    {
        if (Status != UsbAccessRequestStatus.Pending)
        {
            throw new InvalidOperationException($"Only a pending request can be rejected; this one is {Status}.");
        }

        Status = UsbAccessRequestStatus.Rejected;
        DecidedById = Guard.NotEmpty(approverId);
        DecidedByDisplay = Guard.NotNullOrWhiteSpace(approverDisplay, nameof(approverDisplay), maxLength: 256);
        DecidedAt = now;
        DecisionNote = Guard.OptionalMaxLength(note, 1000);
    }

    /// <summary>Ends a live grant early. Returns false when there was nothing to revoke.</summary>
    public bool TryRevoke(Guid actorId, string actorDisplay, string? note, DateTimeOffset now)
    {
        if (Status != UsbAccessRequestStatus.Approved)
        {
            return false;
        }

        Status = UsbAccessRequestStatus.Revoked;
        DecisionNote = Guard.OptionalMaxLength(note, 1000)
            ?? $"Revoked by {Guard.NotNullOrWhiteSpace(actorDisplay, nameof(actorDisplay), maxLength: 256)}.";
        RevokedById = Guard.NotEmpty(actorId);
        RevokedAt = now;

        // The deadline moves to now so that every "was this live at time T?"
        // query gives the same answer whether it asks the status or the window.
        ExpiresAt = now;
        return true;
    }

    /// <summary>Marks a lapsed grant Expired. Returns false if it had not lapsed.</summary>
    public bool TryExpire(DateTimeOffset now)
    {
        if (Status != UsbAccessRequestStatus.Approved || ExpiresAt is not { } expiry || expiry > now)
        {
            return false;
        }

        Status = UsbAccessRequestStatus.Expired;
        return true;
    }

    public Guid? RevokedById { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }
}
