using System.Management;
using System.Runtime.Versioning;
using EndpointAgent.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows;

/// <summary>
/// Raises an event when a USB device arrives or is removed, using WMI device
/// -change notifications.
/// </summary>
/// <remarks>
/// <para>
/// A Windows service in Session 0 has no message-only window to receive
/// <c>WM_DEVICECHANGE</c> without extra plumbing, so this subscribes to the WMI
/// events <c>__InstanceCreationEvent</c> and <c>__InstanceDeletionEvent</c> over
/// <c>Win32_PnPEntity</c> instead. The same information, through an API a
/// service can consume directly.
/// </para>
/// <para>
/// A one-second polling interval is specified on the query: WMI checks for
/// changes at that cadence, which is fast enough for a person plugging in a
/// stick and slow enough not to matter to the machine.
/// </para>
/// <para>
/// This is latency, not enforcement. If the subscription cannot start — WMI
/// unavailable, repository damaged — the agent falls back to its periodic
/// reconcile, so a device still becomes restricted; it simply takes until the
/// next sweep rather than a second or two. Nothing is left permanently
/// unmanaged by this class failing.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsUsbDeviceWatcher(ILogger<WindowsUsbDeviceWatcher> logger) : IUsbDeviceWatcher
{
    private readonly ILogger<WindowsUsbDeviceWatcher> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    private ManagementEventWatcher? _arrival;
    private ManagementEventWatcher? _removal;
    private bool _disposed;

    public event EventHandler<UsbChangeKind>? Changed;

    public bool TryStart()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            _arrival = Subscribe("__InstanceCreationEvent", UsbChangeKind.Arrived);
            _removal = Subscribe("__InstanceDeletionEvent", UsbChangeKind.Removed);

            _logger.LogInformation("Watching for USB device arrival and removal.");
            return true;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Could not subscribe to USB device notifications. USB policy will still be applied on the "
                + "periodic reconcile, with a longer delay before a newly attached device is restricted.");

            Stop();
            return false;
        }
    }

    private ManagementEventWatcher Subscribe(string eventClass, UsbChangeKind kind)
    {
        // Scoped to PnP entities whose device id starts with USB\, so the agent
        // is not woken by every driver event on the machine.
        var query = new WqlEventQuery(
            $"SELECT * FROM {eventClass} WITHIN 1 "
            + "WHERE TargetInstance ISA 'Win32_PnPEntity' "
            + "AND TargetInstance.PNPDeviceID LIKE 'USB\\\\%'");

        var watcher = new ManagementEventWatcher(query);

        watcher.EventArrived += (_, _) =>
        {
            try
            {
                Changed?.Invoke(this, kind);
            }
            catch (Exception ex)
            {
                // A handler throwing on a WMI callback thread would take the
                // process down. The reconcile it was meant to trigger still
                // happens on the timer.
                _logger.LogError(ex, "A USB device-change handler threw.");
            }
        };

        watcher.Start();
        return watcher;
    }

    private void Stop()
    {
        foreach (var watcher in new[] { _arrival, _removal })
        {
            if (watcher is null)
            {
                continue;
            }

            try
            {
                watcher.Stop();
                watcher.Dispose();
            }
            catch (ManagementException)
            {
                // Already gone; nothing useful to do while shutting down.
            }
        }

        _arrival = null;
        _removal = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }
}
