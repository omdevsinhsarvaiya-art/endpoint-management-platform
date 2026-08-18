namespace EndpointPlatform.Domain.Policies;

/// <summary>
/// The closed set of policy types the engine understands.
/// </summary>
/// <remarks>
/// Desired-state architecture: each type declares a target state, the agent
/// evaluates the machine against it and reports COMPLIANT or NON_COMPLIANT with
/// deviations. Adding a policy type means adding a member here plus its agent-side
/// evaluator plus tests. v1 ships one type; the shape generalises.
/// </remarks>
public enum PolicyType
{
    /// <summary>Maximum interactive-idle minutes before the screen must lock.</summary>
    ScreenLockTimeout = 0,
}
