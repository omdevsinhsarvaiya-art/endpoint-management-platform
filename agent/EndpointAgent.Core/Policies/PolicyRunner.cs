using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Policies;

/// <summary>
/// Fetches the device's effective policies, evaluates each, and reports compliance.
/// Read-only: evaluation never changes machine state.
/// </summary>
public sealed class PolicyRunner(
    IAgentApiClient apiClient,
    PolicyEvaluator evaluator,
    ILogger<PolicyRunner> logger)
{
    private readonly IAgentApiClient _apiClient = apiClient;
    private readonly PolicyEvaluator _evaluator = evaluator;
    private readonly ILogger<PolicyRunner> _logger = logger;

    public async Task RunAsync(DeviceCredential credential, CancellationToken cancellationToken = default)
    {
        var fetch = await _apiClient.GetPoliciesAsync(credential, cancellationToken);
        if (!fetch.IsSuccess || fetch.Value is null)
        {
            return;
        }

        if (fetch.Value.Policies.Count == 0)
        {
            return;
        }

        var results = new List<AgentPolicyComplianceItem>();
        foreach (var policy in fetch.Value.Policies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await _evaluator.EvaluateAsync(policy, cancellationToken));
        }

        var report = await _apiClient.PostComplianceAsync(
            new AgentPolicyComplianceReport(results), credential, cancellationToken);

        if (report.IsSuccess)
        {
            _logger.LogInformation("Reported compliance for {Count} policy(ies).", results.Count);
        }
    }
}
