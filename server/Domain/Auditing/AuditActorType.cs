namespace EndpointPlatform.Domain.Auditing;

/// <summary>Which kind of principal performed the audited action.</summary>
public enum AuditActorType
{
    /// <summary>A human administrator authenticated against the Admin API.</summary>
    PlatformUser = 0,

    /// <summary>An enrolled endpoint acting under its device identity on the Agent API.</summary>
    Agent = 1,

    /// <summary>The platform itself: scheduled jobs, seeding, automatic expiry.</summary>
    System = 2,

    /// <summary>An unauthenticated caller. Used when recording rejected attempts.</summary>
    Anonymous = 3,
}
