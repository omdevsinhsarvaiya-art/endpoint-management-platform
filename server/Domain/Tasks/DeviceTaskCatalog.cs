using System.Collections.Frozen;
using EndpointPlatform.Domain.Authorization;

namespace EndpointPlatform.Domain.Tasks;

/// <summary>Static metadata for each <see cref="DeviceTaskType"/>.</summary>
/// <param name="Type">The task type.</param>
/// <param name="RequiredPermission">Permission the caller must hold to queue it.</param>
/// <param name="HighRisk">Whether the UI must confirm and the audit is elevated.</param>
/// <param name="DefaultTimeToLiveSeconds">
/// How long the task stays claimable before it expires. Short for interactive
/// actions (a restart requested an hour ago should not fire when the machine
/// finally checks in), longer for maintenance.
/// </param>
public sealed record DeviceTaskDefinition(
    DeviceTaskType Type,
    string RequiredPermission,
    bool HighRisk,
    int DefaultTimeToLiveSeconds);

/// <summary>
/// The authorization and lifetime policy for every task type, in one place.
/// </summary>
/// <remarks>
/// A task type with no entry here cannot be queued: <see cref="Require"/> throws,
/// which fails closed. That means adding an enum member without deciding its
/// permission is a loud error, not a silently unguarded capability.
/// </remarks>
public static class DeviceTaskCatalog
{
    public static readonly FrozenDictionary<DeviceTaskType, DeviceTaskDefinition> All =
        new DeviceTaskDefinition[]
        {
            new(DeviceTaskType.Ping, Permissions.Task.Execute, HighRisk: false, 300),
            new(DeviceTaskType.RefreshInventory, Permissions.Device.RefreshInventory, HighRisk: false, 3600),
            new(DeviceTaskType.RestartDevice, Permissions.Device.Restart, HighRisk: true, 900),
            new(DeviceTaskType.ShutdownDevice, Permissions.Device.Shutdown, HighRisk: true, 900),
            new(DeviceTaskType.LockDevice, Permissions.Device.Lock, HighRisk: false, 900),
            new(DeviceTaskType.SignOutUser, Permissions.Device.SignOutUser, HighRisk: true, 900),
            new(DeviceTaskType.ControlService, Permissions.Task.Execute, HighRisk: true, 900),
            new(DeviceTaskType.TerminateProcess, Permissions.Task.Execute, HighRisk: true, 600),
            new(DeviceTaskType.InstallPackage, Permissions.Software.Deploy, HighRisk: true, 7200),
        }.ToFrozenDictionary(d => d.Type);

    public static DeviceTaskDefinition Require(DeviceTaskType type) =>
        All.TryGetValue(type, out var definition)
            ? definition
            : throw new InvalidOperationException(
                $"Task type '{type}' has no catalog entry and therefore cannot be queued. " +
                "Add a DeviceTaskDefinition (permission + lifetime) before enabling it.");
}
