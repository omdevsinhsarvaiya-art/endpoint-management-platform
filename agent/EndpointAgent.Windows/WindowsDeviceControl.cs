using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EndpointAgent.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows;

/// <summary>
/// Power and session control via Win32 APIs.
/// </summary>
/// <remarks>
/// <para>
/// No shell command is ever built (ADR-0005). Restart/shutdown use
/// <c>InitiateSystemShutdownExW</c> after enabling <c>SeShutdownPrivilege</c> in
/// the process token; lock uses <c>LockWorkStation</c>; sign-out uses
/// <c>ExitWindowsEx(EWX_LOGOFF)</c>. Every entry point is only reachable through
/// an authenticated, permission-checked, audited server task.
/// </para>
/// <para>
/// These operations require the agent to run elevated (LocalSystem in
/// production). When unelevated, the Win32 call fails and the executor reports the
/// failure - it never silently no-ops.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsDeviceControl(ILogger<WindowsDeviceControl> logger) : IDeviceControl
{
    private readonly ILogger<WindowsDeviceControl> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    // Reason: SHTDN_REASON_MAJOR_OTHER | SHTDN_REASON_MINOR_OTHER | planned.
    private const uint ShutdownReasonPlannedOther = 0x00000000 | 0x00000000 | 0x80000000;
    private const uint EwxLogoff = 0x00000000;

    public Task RestartAsync(int graceSeconds, string? message, CancellationToken cancellationToken = default)
    {
        InitiateShutdown(graceSeconds, message, restart: true);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(int graceSeconds, string? message, CancellationToken cancellationToken = default)
    {
        InitiateShutdown(graceSeconds, message, restart: false);
        return Task.CompletedTask;
    }

    public Task LockAsync(CancellationToken cancellationToken = default)
    {
        if (!LockWorkStation())
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "LockWorkStation failed.");
        }

        return Task.CompletedTask;
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        // EWX_LOGOFF signs out the interactive user. FORCEIFHUNG lets it proceed
        // past applications that stop responding to the close message.
        if (!ExitWindowsEx(EwxLogoff | 0x00000010 /* EWX_FORCEIFHUNG */, ShutdownReasonPlannedOther))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ExitWindowsEx(EWX_LOGOFF) failed.");
        }

        return Task.CompletedTask;
    }

    private void InitiateShutdown(int graceSeconds, string? message, bool restart)
    {
        EnableShutdownPrivilege();

        var timeout = (uint)Math.Clamp(graceSeconds, 0, 3600);

        // bForceAppsClosed = false: give apps the grace period to save.
        var ok = InitiateSystemShutdownExW(
            lpMachineName: null,
            lpMessage: message,
            dwTimeout: timeout,
            bForceAppsClosed: false,
            bRebootAfterShutdown: restart,
            dwReason: ShutdownReasonPlannedOther);

        if (!ok)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error,
                $"InitiateSystemShutdownEx failed (restart={restart}, error={error}).");
        }

        _logger.LogWarning(
            "{Action} initiated with a {Grace}s grace period by an authorized server task.",
            restart ? "Restart" : "Shutdown", timeout);
    }

    /// <summary>Enables SeShutdownPrivilege in the current process token.</summary>
    private void EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var tokenHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed.");
        }

        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "LookupPrivilegeValue failed.");
            }

            var privileges = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED,
            };

            if (!AdjustTokenPrivileges(tokenHandle, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AdjustTokenPrivileges failed.");
            }

            // AdjustTokenPrivileges succeeds even if not all privileges were assigned;
            // ERROR_NOT_ALL_ASSIGNED means the process lacks the privilege (unelevated).
            var last = Marshal.GetLastWin32Error();
            if (last == 1300 /* ERROR_NOT_ALL_ASSIGNED */)
            {
                throw new Win32Exception(last,
                    "SeShutdownPrivilege could not be enabled; the agent is not running elevated.");
            }
        }
        finally
        {
            CloseHandle(tokenHandle);
        }
    }

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitiateSystemShutdownExW(
        string? lpMachineName,
        string? lpMessage,
        uint dwTimeout,
        [MarshalAs(UnmanagedType.Bool)] bool bForceAppsClosed,
        [MarshalAs(UnmanagedType.Bool)] bool bRebootAfterShutdown,
        uint dwReason);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
