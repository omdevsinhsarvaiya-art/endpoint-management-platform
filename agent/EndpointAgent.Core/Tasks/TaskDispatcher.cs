using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Claims queued tasks, routes each to its executor, and reports the result.
/// </summary>
/// <remarks>
/// Every task is executed and reported independently: one task's failure (or a
/// missing executor) never blocks the others, and an executor throwing is turned
/// into a reported failure, never an unhandled exception that kills the agent.
/// </remarks>
public sealed class TaskDispatcher
{
    private readonly IAgentApiClient _apiClient;
    private readonly IReadOnlyDictionary<string, ITaskExecutor> _executors;
    private readonly ILogger<TaskDispatcher> _logger;

    public TaskDispatcher(
        IAgentApiClient apiClient,
        IEnumerable<ITaskExecutor> executors,
        ILogger<TaskDispatcher> logger)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var map = new Dictionary<string, ITaskExecutor>(StringComparer.Ordinal);
        foreach (var executor in executors)
        {
            map[executor.TaskType] = executor;
        }

        _executors = map;
    }

    /// <summary>Claims and runs all currently-queued tasks for this device.</summary>
    public async Task RunPendingAsync(DeviceCredential credential, CancellationToken cancellationToken = default)
    {
        var claim = await _apiClient.ClaimTasksAsync(credential, cancellationToken);

        if (!claim.IsSuccess || claim.Value is null || claim.Value.Tasks.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Claimed {Count} task(s) for execution.", claim.Value.Tasks.Count);

        foreach (var task in claim.Value.Tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ExecuteOneAsync(task, cancellationToken);
            await _apiClient.PostTaskResultAsync(task.TaskId, result, credential, cancellationToken);
        }
    }

    private async Task<AgentTaskResult> ExecuteOneAsync(AgentTask task, CancellationToken cancellationToken)
    {
        if (!_executors.TryGetValue(task.Type, out var executor))
        {
            _logger.LogWarning("No executor for task type '{Type}' (task {TaskId}).", task.Type, task.TaskId);
            return new AgentTaskResult(false, $"Unsupported task type '{task.Type}'.", null);
        }

        try
        {
            _logger.LogInformation("Executing task {TaskId} ({Type}).", task.TaskId, task.Type);
            return await executor.ExecuteAsync(task, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Task {TaskId} ({Type}) threw.", task.TaskId, task.Type);
            return new AgentTaskResult(false, $"Execution failed: {ex.GetType().Name}.", null);
        }
    }
}
