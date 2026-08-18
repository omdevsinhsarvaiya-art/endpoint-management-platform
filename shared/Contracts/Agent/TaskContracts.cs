namespace EndpointPlatform.Contracts.Agent;

/// <summary>One task handed to an agent for execution.</summary>
/// <param name="TaskId">Server task identity; echoed back with the result.</param>
/// <param name="Type">Task type name (matches the server's DeviceTaskType).</param>
/// <param name="PayloadJson">Typed payload document, or null for payload-free tasks.</param>
public sealed record AgentTask(Guid TaskId, string Type, string? PayloadJson);

/// <summary>Response to the agent's task poll: zero or more tasks to run now.</summary>
public sealed record AgentTaskListResponse(IReadOnlyList<AgentTask> Tasks);

/// <summary>Result the agent posts back after running a task.</summary>
/// <param name="Succeeded">Whether the operation completed successfully.</param>
/// <param name="Message">Short human-readable outcome or failure reason (no secrets).</param>
/// <param name="ResultJson">Optional structured result document (no secrets).</param>
public sealed record AgentTaskResult(bool Succeeded, string? Message, string? ResultJson);
