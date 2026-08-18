using System.Runtime.Versioning;
using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EndpointAgent.Windows;

/// <summary>
/// Reads Windows Update history via the Windows Update Agent (WUA) COM API and the
/// reboot-required state from the registry.
/// </summary>
/// <remarks>
/// <para>
/// Uses late-bound COM (<c>Microsoft.Update.Session</c>) so no interop assembly is
/// needed. <c>IUpdateSearcher.QueryHistory</c> reads the LOCAL history store -
/// fast and offline, unlike an online <c>Search</c> for pending updates, which is
/// deliberately not done here (see the interface remarks). No shell (ADR-0005).
/// </para>
/// <para>
/// If WUA is unavailable (disabled service, container), history comes back empty
/// and only the reboot flag is reported; the collector never throws into the
/// inventory path.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateCollector(ILogger<WindowsUpdateCollector> logger) : IWindowsUpdateCollector
{
    private const int MaxHistoryEntries = 100;

    private readonly ILogger<WindowsUpdateCollector> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public ValueTask<InventoryWindowsUpdate> CollectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rebootRequired = ReadRebootRequired();
        var history = ReadHistory();

        return ValueTask.FromResult(new InventoryWindowsUpdate(rebootRequired, history));
    }

    private bool ReadRebootRequired()
    {
        // Either of these keys existing means a reboot is pending.
        return KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")
            || KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
    }

    private bool KeyExists(string path)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read reboot-pending key {Path}.", path);
            return false;
        }
    }

    private List<InventoryUpdateHistoryEntry> ReadHistory()
    {
        var entries = new List<InventoryUpdateHistoryEntry>();

        var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
        if (sessionType is null)
        {
            _logger.LogDebug("Windows Update Agent COM is unavailable; reporting no update history.");
            return entries;
        }

        object? session = null;
        try
        {
            session = Activator.CreateInstance(sessionType);
            dynamic searcher = ((dynamic)session!).CreateUpdateSearcher();

            int total = searcher.GetTotalHistoryCount();
            if (total <= 0)
            {
                return entries;
            }

            var take = Math.Min(total, MaxHistoryEntries);
            dynamic history = searcher.QueryHistory(0, take);

            int count = history.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic entry = history[i];
                try
                {
                    string title = entry.Title ?? "(untitled update)";
                    DateTimeOffset? date = TryReadDate(entry);
                    var operation = ((int)entry.Operation) switch
                    {
                        1 => "Installation",
                        2 => "Uninstallation",
                        _ => "Other",
                    };
                    var result = ((int)entry.ResultCode) switch
                    {
                        1 => "InProgress",
                        2 => "Succeeded",
                        3 => "SucceededWithErrors",
                        4 => "Failed",
                        5 => "Aborted",
                        _ => "Unknown",
                    };

                    entries.Add(new InventoryUpdateHistoryEntry(
                        title.Length > 384 ? title[..384] : title, date, operation, result));
                }
                catch (Exception ex) when (ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                               or System.Runtime.InteropServices.COMException)
                {
                    _logger.LogDebug(ex, "Skipping an unreadable update history entry.");
                }
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                       or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Windows Update history query failed; reporting what was read.");
        }
        finally
        {
            if (session is not null && System.Runtime.InteropServices.Marshal.IsComObject(session))
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(session);
            }
        }

        return entries;
    }

    private static DateTimeOffset? TryReadDate(dynamic entry)
    {
        try
        {
            DateTime date = entry.Date;
            // WUA returns UTC.
            return date == default ? null : new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc));
        }
        catch (Exception ex) when (ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                       or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
