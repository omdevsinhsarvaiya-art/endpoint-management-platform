namespace EndpointPlatform.Domain.Auditing;

/// <summary>Outcome of an audited action.</summary>
/// <remarks>
/// <see cref="Denied"/> is kept distinct from <see cref="Failure"/> on purpose: a
/// permission denial is a security signal worth alerting on, whereas a failure is
/// usually an operational problem. Collapsing them would hide attempted privilege
/// escalation inside ordinary error noise.
/// </remarks>
public enum AuditResult
{
    Success = 0,

    /// <summary>The action was attempted and did not complete.</summary>
    Failure = 1,

    /// <summary>The caller was authenticated but lacked the required permission.</summary>
    Denied = 2,
}
