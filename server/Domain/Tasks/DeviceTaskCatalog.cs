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
/// <param name="MinimumAgentVersion">
/// The oldest agent build that has an executor for this task, or null when every
/// supported agent can run it.
/// </param>
public sealed record DeviceTaskDefinition(
    DeviceTaskType Type,
    string RequiredPermission,
    bool HighRisk,
    int DefaultTimeToLiveSeconds,
    string? MinimumAgentVersion = null);

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

            // Resolution happens on the endpoint, so this needs an agent that has
            // the executor. Gated rather than best-effort: an older agent would
            // report an unknown task type, which reads in the console exactly like
            // the application failing to stop, and the operator would retry
            // something that cannot work.
            new(DeviceTaskType.StopApplication, Permissions.Task.Execute, HighRisk: true, 600,
                MinimumAgentVersion: "1.6.0"),

            // Software.Deploy, not Task.Execute: removing installed software is a
            // change to what the machine runs, which is the decision the deploy
            // permission already entrusts to a role. The stop step inside it is
            // incidental to the uninstall, not the point of it. Gated on the first
            // agent with the executor, for the same reason as StopApplication.
            //
            // 1.10.0 is the EXECUTION boundary: the release carrying
            // RemoveApplicationExecutor. It is deliberately not 1.9.0, the
            // INVENTORY boundary named in SoftwareRemovability.Evaluate, which is
            // where application discovery shipped and therefore where IdentityKind
            // and PackageFullName begin to exist. The two answer different
            // questions -- what a row is, versus what the endpoint can do about it
            // -- so an agent on 1.9.x reports rows SoftwareRemovability rightly
            // calls removable and must still be refused here. Do not reconcile
            // them; the console mirrors this one as MINIMUM_REMOVE_AGENT_VERSION
            // in dashboard/src/pages/softwareView.ts and must be changed with it.
            //
            // An hour, and the same hour UpdateAgent uses, for the same reason: the
            // TTL is measured from queue time, and TaskExpirySweeper expires
            // Delivered tasks as well as Queued ones, so the budget has to cover
            // both the wait for a device to claim the task and the removal itself.
            // The removal alone is long: the endpoint's package path waits up to
            // ten minutes for the deployment engine, and an MSI uninstall is
            // unbounded because the product's own custom actions run inside it. At
            // half an hour a task claimed twenty-five minutes after it was queued
            // is expired mid-uninstall, and the genuine result the agent then
            // reports is rejected -- which reads in the console exactly like an
            // application that would not uninstall.
            new(DeviceTaskType.RemoveApplication, Permissions.Software.Deploy, HighRisk: true, 3600,
                MinimumAgentVersion: "1.10.0"),
            // Short TTL. A grant is time-boxed from the moment it is issued, so a
            // policy task that sat queued for an hour would arrive describing a
            // window that has largely elapsed; better to expire it and have the
            // administrator re-issue against the machine that is actually there.
            // The safe state needs no delivery: an endpoint that never receives
            // this task keeps everything restricted.
            new(DeviceTaskType.ApplyUsbPolicy, Permissions.Usb.Manage, HighRisk: true, 900),

            new(DeviceTaskType.InstallPackage, Permissions.Software.Deploy, HighRisk: true, 7200),
            // Software.Deploy on purpose: updating the agent IS deploying
            // software fleet-wide, and the roles trusted with one are exactly
            // the roles trusted with the other.
            new(DeviceTaskType.UpdateAgent, Permissions.Software.Deploy, HighRisk: true, 3600),

            // Local account management (Phase 4 write side). All high-risk, interactive TTL:
            // an account change requested an hour ago should not fire when a laptop finally
            // reappears - the operator would re-issue it.
            new(DeviceTaskType.CreateLocalUser, Permissions.LocalUser.Create, HighRisk: true, 900),
            new(DeviceTaskType.DeleteLocalUser, Permissions.LocalUser.Delete, HighRisk: true, 900),
            new(DeviceTaskType.EnableLocalUser, Permissions.LocalUser.Disable, HighRisk: true, 900),
            new(DeviceTaskType.DisableLocalUser, Permissions.LocalUser.Disable, HighRisk: true, 900),
            new(DeviceTaskType.ResetLocalUserPassword, Permissions.LocalUser.ResetPassword, HighRisk: true, 900),
            new(DeviceTaskType.ForceLocalUserPasswordChange, Permissions.LocalUser.ForcePasswordChange, HighRisk: true, 900),
            new(DeviceTaskType.ChangeLocalUserType, Permissions.LocalUser.ChangeType, HighRisk: true, 900),
            new(DeviceTaskType.AddLocalUserToGroup, Permissions.Group.Manage, HighRisk: true, 900),
            new(DeviceTaskType.RemoveLocalUserFromGroup, Permissions.Group.Manage, HighRisk: true, 900),
        new(DeviceTaskType.ApplyLocalAdminElevation, Permissions.LocalUser.Elevate, HighRisk: true, 900),

            // Driver installation needs an agent that has the executor. An older one
            // would claim the task, fail it as an unknown type, and leave a failed
            // task indistinguishable from a driver that would not install -- so the
            // server refuses to queue it at all. Maintenance TTL: unlike an
            // interactive action, a driver install that fires when a laptop
            // reappears is still the right thing to do.
            new(DeviceTaskType.InstallDriverPackage, Permissions.Driver.Manage, HighRisk: true, 3600,
                MinimumAgentVersion: "1.3.0"),
        }.ToFrozenDictionary(d => d.Type);

    /// <summary>
    /// Whether an agent reporting <paramref name="agentVersion"/> can run this task.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fails closed twice over. A definition with no minimum admits everything; a
    /// definition with one admits nothing whose version cannot be parsed and
    /// compared, because an agent that will not say what it is has not demonstrated
    /// that it can do the work.
    /// </para>
    /// <para>
    /// This is a pre-dispatch courtesy, not the safety boundary. The agent still
    /// fails closed on a task type it has no executor for, and that behaviour is
    /// unchanged -- this only stops the server creating work it knows will fail.
    /// </para>
    /// </remarks>
    public static bool IsSupportedBy(DeviceTaskDefinition definition, string? agentVersion)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.MinimumAgentVersion is not { } minimum)
        {
            return true;
        }

        return Version.TryParse(Trim(agentVersion), out var reported)
            && Version.TryParse(minimum, out var required)
            && reported >= required;
    }

    /// <summary>Drops any pre-release or build suffix, e.g. "1.3.0-beta.2" or "1.3.0+ci".</summary>
    private static string? Trim(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var value = version.Trim();
        var cut = value.IndexOfAny(['-', '+', ' ']);
        return cut < 0 ? value : value[..cut];
    }

    public static DeviceTaskDefinition Require(DeviceTaskType type) =>
        All.TryGetValue(type, out var definition)
            ? definition
            : throw new InvalidOperationException(
                $"Task type '{type}' has no catalog entry and therefore cannot be queued. " +
                "Add a DeviceTaskDefinition (permission + lifetime) before enabling it.");
}
