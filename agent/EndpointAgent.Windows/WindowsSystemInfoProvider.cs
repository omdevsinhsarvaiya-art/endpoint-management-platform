using System.Management;
using System.Runtime.Versioning;
using EndpointAgent.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows;

/// <summary>
/// Reads machine facts through native .NET and CIM/WMI.
/// </summary>
/// <remarks>
/// <para>
/// No process is launched and no command string is built. Every value comes from a
/// managed API or a parameterless CIM query against a fixed class name, so there is
/// no place for machine data to be interpreted as a command. This is the pattern
/// every collector in this assembly follows - see ADR-0005.
/// </para>
/// <para>
/// CIM queries are comparatively slow (tens of milliseconds each) and are called on
/// the heartbeat path, so results that cannot change while the process is running
/// are cached for the lifetime of the agent.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemInfoProvider(ILogger<WindowsSystemInfoProvider> logger) : ISystemInfoProvider
{
    private readonly ILogger<WindowsSystemInfoProvider> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    private string? _cachedOsDescription;
    private string? _cachedMachineIdentifier;

    public string GetHostName() => Environment.MachineName;

    public ValueTask<string> GetOperatingSystemDescriptionAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedOsDescription is not null)
        {
            return ValueTask.FromResult(_cachedOsDescription);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Fixed query text against a fixed class - no interpolation of any value
        // that could come from outside this process.
        var description = QuerySingle(
            "SELECT Caption, BuildNumber FROM Win32_OperatingSystem",
            static row =>
            {
                var caption = row["Caption"]?.ToString()?.Trim();
                var build = row["BuildNumber"]?.ToString()?.Trim();
                return string.IsNullOrEmpty(caption)
                    ? null
                    : string.IsNullOrEmpty(build) ? caption : $"{caption} (build {build})";
            });

        // RuntimeInformation.OSDescription always works, so a WMI failure degrades
        // detail rather than failing the heartbeat outright.
        _cachedOsDescription = description ?? System.Runtime.InteropServices.RuntimeInformation.OSDescription;

        return ValueTask.FromResult(_cachedOsDescription);
    }

    public ValueTask<string> GetMachineIdentifierAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedMachineIdentifier is not null)
        {
            return ValueTask.FromResult(_cachedMachineIdentifier);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Win32_ComputerSystemProduct.UUID is the SMBIOS system UUID: stable across
        // reinstalls and unique per machine on real hardware. It is NOT a secret and
        // is NOT used for authentication - it only lets the server recognise that a
        // re-enrolling machine already has a device record.
        var uuid = QuerySingle(
            "SELECT UUID FROM Win32_ComputerSystemProduct",
            static row => row["UUID"]?.ToString()?.Trim());

        if (string.IsNullOrWhiteSpace(uuid) || uuid == "00000000-0000-0000-0000-000000000000")
        {
            _logger.LogWarning(
                "SMBIOS system UUID is unavailable or a null placeholder; falling back to machine name. "
                + "Duplicate-device detection will be less reliable on this endpoint.");

            _cachedMachineIdentifier = Environment.MachineName;
            return ValueTask.FromResult(_cachedMachineIdentifier);
        }

        _cachedMachineIdentifier = uuid;
        return ValueTask.FromResult(_cachedMachineIdentifier);
    }

    private T? QuerySingle<T>(string wqlQuery, Func<ManagementBaseObject, T?> project)
        where T : class
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ObjectQuery(wqlQuery));
            using var results = searcher.Get();

            foreach (var row in results)
            {
                using (row)
                {
                    var value = project(row);
                    if (value is not null)
                    {
                        return value;
                    }
                }
            }

            return null;
        }
        catch (ManagementException ex)
        {
            // A broken WMI repository is a real and common endpoint fault. It must
            // degrade the inventory, never crash the agent service.
            _logger.LogWarning(ex, "CIM query failed: {Query}", wqlQuery);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied running CIM query: {Query}", wqlQuery);
            return null;
        }
    }
}
