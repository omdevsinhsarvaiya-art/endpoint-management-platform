namespace EndpointPlatform.Contracts.Agent;

/// <summary>One effective policy the agent must evaluate.</summary>
/// <param name="PolicyId">Server policy identity.</param>
/// <param name="PolicyVersionId">Exact version to evaluate against.</param>
/// <param name="VersionNumber">Human-facing version number.</param>
/// <param name="Type">Policy type name (matches the server PolicyType enum).</param>
/// <param name="DesiredStateJson">Type-specific desired-state document.</param>
public sealed record AgentPolicy(
    Guid PolicyId,
    Guid PolicyVersionId,
    int VersionNumber,
    string Type,
    string DesiredStateJson);

/// <summary>Response to the agent's policy pull.</summary>
public sealed record AgentPolicyListResponse(IReadOnlyList<AgentPolicy> Policies);

/// <summary>One compliance result the agent reports back.</summary>
/// <param name="PolicyId">Policy evaluated.</param>
/// <param name="PolicyVersionId">Version evaluated.</param>
/// <param name="VersionNumber">Version number evaluated.</param>
/// <param name="State">"Compliant", "NonCompliant" or "Unknown".</param>
/// <param name="Deviations">Human-readable deviation descriptions (empty when compliant).</param>
public sealed record AgentPolicyComplianceItem(
    Guid PolicyId,
    Guid PolicyVersionId,
    int VersionNumber,
    string State,
    IReadOnlyList<string> Deviations);

/// <summary>The agent's batch compliance report.</summary>
public sealed record AgentPolicyComplianceReport(IReadOnlyList<AgentPolicyComplianceItem> Results);
