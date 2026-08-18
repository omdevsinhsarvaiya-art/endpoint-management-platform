using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Executes one kind of task. Registered by <see cref="TaskType"/>; the dispatcher
/// routes each incoming <see cref="AgentTask"/> to the matching executor.
/// </summary>
/// <remarks>
/// One executor per task type keeps each privileged capability a small, separately
/// reviewable unit. An unknown task type has no executor and is reported failed,
/// never guessed at.
/// </remarks>
public interface ITaskExecutor
{
    /// <summary>The task type name this executor handles (matches the server enum name).</summary>
    string TaskType { get; }

    Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default);
}
