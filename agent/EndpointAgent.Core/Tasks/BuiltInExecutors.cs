using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>Benign executor that proves the pipeline end to end.</summary>
public sealed class PingTaskExecutor : ITaskExecutor
{
    public string TaskType => "Ping";

    public Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentTaskResult(true, "pong", null));
}

/// <summary>Collects and uploads a fresh inventory in response to a task.</summary>
public sealed class RefreshInventoryTaskExecutor(
    IInventoryCollector collector,
    IAgentApiClient apiClient,
    IDeviceCredentialStore credentialStore) : ITaskExecutor
{
    private readonly IInventoryCollector _collector = collector;
    private readonly IAgentApiClient _apiClient = apiClient;
    private readonly IDeviceCredentialStore _credentialStore = credentialStore;

    public string TaskType => "RefreshInventory";

    public async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        var credential = await _credentialStore.LoadAsync(cancellationToken);
        if (credential is null)
        {
            return new AgentTaskResult(false, "No device credential available.", null);
        }

        var report = await _collector.CollectAsync(cancellationToken);
        var result = await _apiClient.UploadInventoryAsync(report, credential, cancellationToken);

        return result.IsSuccess
            ? new AgentTaskResult(true, "Inventory uploaded.", null)
            : new AgentTaskResult(false, $"Inventory upload failed ({result.Status}).", null);
    }
}

/// <summary>Base for the power/session control executors; parses the shared payload.</summary>
public abstract class DeviceControlTaskExecutor(IDeviceControl deviceControl, ILogger logger) : ITaskExecutor
{
    protected IDeviceControl DeviceControl { get; } = deviceControl;
    protected ILogger Logger { get; } = logger;

    public abstract string TaskType { get; }

    public abstract Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default);

    /// <summary>Parses the grace/message payload; defaults to a 30s grace when absent.</summary>
    protected static (int GraceSeconds, string? Message) ParseGrace(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return (30, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var grace = root.TryGetProperty("graceSeconds", out var g) && g.TryGetInt32(out var seconds)
                ? Math.Clamp(seconds, 0, 3600)
                : 30;
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            return (grace, message);
        }
        catch (JsonException)
        {
            return (30, null);
        }
    }
}

public sealed class RestartTaskExecutor(IDeviceControl deviceControl, ILogger<RestartTaskExecutor> logger)
    : DeviceControlTaskExecutor(deviceControl, logger)
{
    public override string TaskType => "RestartDevice";

    public override async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        var (grace, message) = ParseGrace(task.PayloadJson);
        await DeviceControl.RestartAsync(grace, message, cancellationToken);
        return new AgentTaskResult(true, $"Restart scheduled in {grace}s.", null);
    }
}

public sealed class ShutdownTaskExecutor(IDeviceControl deviceControl, ILogger<ShutdownTaskExecutor> logger)
    : DeviceControlTaskExecutor(deviceControl, logger)
{
    public override string TaskType => "ShutdownDevice";

    public override async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        var (grace, message) = ParseGrace(task.PayloadJson);
        await DeviceControl.ShutdownAsync(grace, message, cancellationToken);
        return new AgentTaskResult(true, $"Shutdown scheduled in {grace}s.", null);
    }
}

public sealed class LockTaskExecutor(IDeviceControl deviceControl, ILogger<LockTaskExecutor> logger)
    : DeviceControlTaskExecutor(deviceControl, logger)
{
    public override string TaskType => "LockDevice";

    public override async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        await DeviceControl.LockAsync(cancellationToken);
        return new AgentTaskResult(true, "Workstation locked.", null);
    }
}

public sealed class SignOutTaskExecutor(IDeviceControl deviceControl, ILogger<SignOutTaskExecutor> logger)
    : DeviceControlTaskExecutor(deviceControl, logger)
{
    public override string TaskType => "SignOutUser";

    public override async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        await DeviceControl.SignOutAsync(cancellationToken);
        return new AgentTaskResult(true, "Interactive user signed out.", null);
    }
}
