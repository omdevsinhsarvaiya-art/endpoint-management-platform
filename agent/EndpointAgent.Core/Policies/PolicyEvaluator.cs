using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Policies;

/// <summary>
/// Evaluates the machine against its assigned policies and produces compliance
/// results. Read-only: it never remediates. Silent destructive remediation is
/// explicitly out of scope (spec) - the platform surfaces non-compliance and an
/// administrator takes an explicit, audited remediation action.
/// </summary>
public sealed class PolicyEvaluator(
    IScreenLockPolicyReader screenLockReader,
    ILogger<PolicyEvaluator> logger)
{
    private readonly IScreenLockPolicyReader _screenLockReader = screenLockReader;
    private readonly ILogger<PolicyEvaluator> _logger = logger;

    public async Task<AgentPolicyComplianceItem> EvaluateAsync(
        AgentPolicy policy, CancellationToken cancellationToken = default)
    {
        try
        {
            return policy.Type switch
            {
                "ScreenLockTimeout" => await EvaluateScreenLockAsync(policy, cancellationToken),
                _ => Unknown(policy, $"Unsupported policy type '{policy.Type}'."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Policy {PolicyId} evaluation threw.", policy.PolicyId);
            return Unknown(policy, "Evaluation failed.");
        }
    }

    private async Task<AgentPolicyComplianceItem> EvaluateScreenLockAsync(
        AgentPolicy policy, CancellationToken cancellationToken)
    {
        int maxSeconds;
        try
        {
            using var doc = JsonDocument.Parse(policy.DesiredStateJson);
            maxSeconds = doc.RootElement.GetProperty("maxTimeoutSeconds").GetInt32();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Unknown(policy, "Malformed desired state.");
        }

        var actual = await _screenLockReader.GetScreenLockTimeoutSecondsAsync(cancellationToken);

        if (actual is null)
        {
            return Unknown(policy, "Screen-lock timeout is not configured or could not be read.");
        }

        if (actual.Value <= maxSeconds && actual.Value > 0)
        {
            return Compliant(policy);
        }

        var deviation = actual.Value <= 0
            ? "Screen never locks automatically."
            : $"Screen locks after {actual.Value}s; policy requires at most {maxSeconds}s.";

        return new AgentPolicyComplianceItem(
            policy.PolicyId, policy.PolicyVersionId, policy.VersionNumber, "NonCompliant", [deviation]);
    }

    private static AgentPolicyComplianceItem Compliant(AgentPolicy p) =>
        new(p.PolicyId, p.PolicyVersionId, p.VersionNumber, "Compliant", []);

    private static AgentPolicyComplianceItem Unknown(AgentPolicy p, string reason) =>
        new(p.PolicyId, p.PolicyVersionId, p.VersionNumber, "Unknown", [reason]);
}
