namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Read-only facts about the machine the agent is running on.
/// </summary>
/// <remarks>
/// An abstraction so that agent logic can be unit-tested on any OS with a fake, and
/// so that the single Windows implementation (WMI/CIM and Win32) stays confined to
/// <c>EndpointAgent.Windows</c>. Phase 0 exposes only what a heartbeat needs;
/// Phase 2 extends this with full hardware and network inventory.
/// </remarks>
public interface ISystemInfoProvider
{
    /// <summary>NetBIOS/DNS host name of the machine.</summary>
    string GetHostName();

    /// <summary>Operating system caption and build, e.g. "Windows 11 Pro 26200".</summary>
    ValueTask<string> GetOperatingSystemDescriptionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// A stable, machine-specific identifier used to detect re-enrollment of a
    /// machine that already has a device record.
    /// </summary>
    ValueTask<string> GetMachineIdentifierAsync(CancellationToken cancellationToken = default);
}
