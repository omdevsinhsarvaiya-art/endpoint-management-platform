using System.Runtime.Versioning;
using EndpointAgent.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EndpointAgent.Windows;

/// <summary>
/// Reads the effective screen-lock timeout from the registry (read-only).
/// </summary>
/// <remarks>
/// Prefers the machine policy (HKLM ...\Policies\System\InactivityTimeoutSecs,
/// set by GPO/Intune), falling back to the interactive screen-saver settings
/// (HKCU\Control Panel\Desktop: ScreenSaveActive + ScreenSaverIsSecure +
/// ScreenSaveTimeOut). Returns null when nothing is configured.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsScreenLockPolicyReader(ILogger<WindowsScreenLockPolicyReader> logger)
    : IScreenLockPolicyReader
{
    private readonly ILogger<WindowsScreenLockPolicyReader> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public ValueTask<int?> GetScreenLockTimeoutSecondsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Machine inactivity policy takes precedence when present.
            using var policyKey = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            if (policyKey?.GetValue("InactivityTimeoutSecs") is int secs && secs > 0)
            {
                return ValueTask.FromResult<int?>(secs);
            }

            // Fall back to the interactive screen-saver lock.
            using var desktop = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (desktop is null)
            {
                return ValueTask.FromResult<int?>(null);
            }

            var active = (desktop.GetValue("ScreenSaveActive") as string) == "1";
            var secure = (desktop.GetValue("ScreenSaverIsSecure") as string) == "1";
            var timeout = int.TryParse(desktop.GetValue("ScreenSaveTimeOut") as string, out var t) ? t : 0;

            // Only counts as a screen LOCK when the saver is active AND secure.
            if (active && secure && timeout > 0)
            {
                return ValueTask.FromResult<int?>(timeout);
            }

            return ValueTask.FromResult<int?>(null);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Screen-lock timeout unreadable.");
            return ValueTask.FromResult<int?>(null);
        }
    }
}
