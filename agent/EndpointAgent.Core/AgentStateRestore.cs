using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core;

/// <summary>
/// One-shot recovery of identity state after an agent update.
/// </summary>
/// <remarks>
/// <para>
/// Before scheduling an update install, the update executor snapshots the
/// state files (device credential, enrollment state — the files that ARE this
/// device's identity) into a backup folder inside the protected state
/// directory. This runs at every service start and closes the loop: if an
/// installer step — most notably the 1.0.0 MSI's uninstall, which removes the
/// state folder's contents during a major upgrade — destroyed the live files,
/// they are restored from the snapshot, and the updated agent carries on as the
/// same device with the same credential instead of re-enrolling as a stranger.
/// </para>
/// <para>
/// The backup is consumed whole: restored or not, the folder is deleted after
/// inspection, so a stale snapshot can never resurrect an old identity months
/// later. Live files always win — a backup never overwrites a file that exists.
/// </para>
/// </remarks>
public static class AgentStateRestore
{
    public const string BackupDirectoryName = "update-backup";

    public static void RestoreIfNeeded(ILogger logger)
    {
        var stateDir = AgentPaths.StateDirectory;
        var backupDir = Path.Combine(stateDir, BackupDirectoryName);

        try
        {
            if (!Directory.Exists(backupDir))
            {
                return;
            }

            var restored = 0;
            foreach (var backupFile in Directory.EnumerateFiles(backupDir, "*.bin"))
            {
                var livePath = Path.Combine(stateDir, Path.GetFileName(backupFile));
                if (!File.Exists(livePath))
                {
                    File.Copy(backupFile, livePath);
                    restored++;
                }
            }

            Directory.Delete(backupDir, recursive: true);

            if (restored > 0)
            {
                logger.LogWarning(
                    "Restored {Count} state file(s) from the update backup — the installer removed live state; "
                    + "device identity and credential were preserved by the snapshot.", restored);
            }
            else
            {
                logger.LogInformation("Update backup found with nothing to restore; snapshot discarded.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never let recovery break startup: a failed restore leaves the agent
            // exactly where it would have been without one.
            logger.LogError(ex, "Update-backup restore failed; continuing with whatever state exists.");
        }
    }
}
