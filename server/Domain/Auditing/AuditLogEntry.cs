using System.Net;
using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Auditing;

/// <summary>
/// One immutable record of a privileged action.
/// </summary>
/// <remarks>
/// <para>
/// Append-only by construction and by database policy. Every property has a
/// private setter and the type exposes no mutators, so there is no code path in
/// the application that can alter a written entry. That is defence in depth on top
/// of the real control: the runtime database role is granted only INSERT and
/// SELECT on <c>audit_log_entries</c>, and a trigger raises an exception on any
/// UPDATE or DELETE. See <c>docs/threat-model.md</c>.
/// </para>
/// <para>
/// <see cref="PreviousState"/> and <see cref="NewState"/> are JSON documents stored
/// in <c>jsonb</c>. They must be redacted by the caller before construction —
/// nothing here inspects them, so a secret placed in one would be persisted. The
/// <c>AuditStateRedactor</c> in the infrastructure layer is the supported way to
/// build them.
/// </para>
/// </remarks>
public sealed class AuditLogEntry : Entity
{
    private AuditLogEntry()
    {
        Action = null!;
        ActorDisplay = null!;
    }

    private AuditLogEntry(
        Guid organizationId,
        DateTimeOffset occurredAt,
        AuditActorType actorType,
        Guid? actorId,
        string actorDisplay,
        string action,
        AuditResult result)
    {
        OrganizationId = organizationId;
        OccurredAt = occurredAt;
        ActorType = actorType;
        ActorId = actorId;
        ActorDisplay = Guard.NotNullOrWhiteSpace(actorDisplay, nameof(actorDisplay), maxLength: 320);
        Action = Guard.NotNullOrWhiteSpace(action, nameof(action), maxLength: 128).ToLowerInvariant();
        Result = result;
    }

    public Guid OrganizationId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public AuditActorType ActorType { get; private set; }

    /// <summary>Platform user id or device id, depending on <see cref="ActorType"/>.</summary>
    public Guid? ActorId { get; private set; }

    /// <summary>
    /// Human-readable actor captured at the time of the action (an email address or
    /// a device hostname). Denormalised deliberately: the audit trail must stay
    /// readable after the referenced account is renamed or deleted.
    /// </summary>
    public string ActorDisplay { get; private set; }

    /// <summary>Dotted action key, e.g. <c>user.change_account_type</c>.</summary>
    public string Action { get; private set; }

    public AuditResult Result { get; private set; }

    /// <summary>The endpoint the action was performed against, when applicable.</summary>
    public Guid? DeviceId { get; private set; }

    public string? DeviceDisplay { get; private set; }

    /// <summary>Type of the object acted upon, e.g. <c>windows_local_user</c>.</summary>
    public string? TargetType { get; private set; }

    public string? TargetId { get; private set; }

    public string? TargetDisplay { get; private set; }

    /// <summary>Redacted JSON snapshot before the change.</summary>
    public string? PreviousState { get; private set; }

    /// <summary>Redacted JSON snapshot after the change.</summary>
    public string? NewState { get; private set; }

    /// <summary>Populated for <see cref="AuditResult.Failure"/> and <see cref="AuditResult.Denied"/>.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Permission that was required, recorded for denials and high-risk successes.</summary>
    public string? RequiredPermission { get; private set; }

    public IPAddress? SourceIp { get; private set; }

    public string? UserAgent { get; private set; }

    /// <summary>Ties this entry to the HTTP request and to log lines emitted while handling it.</summary>
    public string? CorrelationId { get; private set; }

    public static AuditLogEntryBuilder For(
        Guid organizationId,
        DateTimeOffset occurredAt,
        AuditActorType actorType,
        Guid? actorId,
        string actorDisplay,
        string action,
        AuditResult result) =>
        new(new AuditLogEntry(organizationId, occurredAt, actorType, actorId, actorDisplay, action, result));

    /// <summary>
    /// Fluent builder. It is the only way to populate the optional fields, which
    /// keeps <see cref="AuditLogEntry"/> itself free of public setters.
    /// </summary>
    public sealed class AuditLogEntryBuilder(AuditLogEntry entry)
    {
        public AuditLogEntryBuilder OnDevice(Guid? deviceId, string? deviceDisplay)
        {
            entry.DeviceId = deviceId;
            entry.DeviceDisplay = Guard.OptionalMaxLength(deviceDisplay, 256);
            return this;
        }

        public AuditLogEntryBuilder OnTarget(string? targetType, string? targetId, string? targetDisplay)
        {
            entry.TargetType = Guard.OptionalMaxLength(targetType, 64);
            entry.TargetId = Guard.OptionalMaxLength(targetId, 256);
            entry.TargetDisplay = Guard.OptionalMaxLength(targetDisplay, 256);
            return this;
        }

        /// <summary>
        /// Records the before/after snapshots. Both arguments must already be
        /// redacted JSON; this method does not inspect them.
        /// </summary>
        public AuditLogEntryBuilder WithStateChange(string? previousStateJson, string? newStateJson)
        {
            entry.PreviousState = previousStateJson;
            entry.NewState = newStateJson;
            return this;
        }

        public AuditLogEntryBuilder WithFailureReason(string? reason)
        {
            entry.FailureReason = Guard.OptionalMaxLength(reason, 1024);
            return this;
        }

        public AuditLogEntryBuilder Requiring(string? permission)
        {
            entry.RequiredPermission = Guard.OptionalMaxLength(permission, 64);
            return this;
        }

        public AuditLogEntryBuilder FromRequest(IPAddress? sourceIp, string? userAgent, string? correlationId)
        {
            entry.SourceIp = sourceIp;
            entry.UserAgent = Guard.OptionalMaxLength(userAgent, 512);
            entry.CorrelationId = Guard.OptionalMaxLength(correlationId, 128);
            return this;
        }

        public AuditLogEntry Build() => entry;
    }
}
