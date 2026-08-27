using System.IO.Compression;

namespace EndpointAgent.Core.Drivers;

/// <summary>Why an archive was refused, or <see cref="Ok"/> when it was accepted.</summary>
public enum DriverArchiveResult
{
    Ok = 0,

    /// <summary>An entry tried to write outside the extraction directory.</summary>
    PathTraversal = 1,

    /// <summary>An entry carried an absolute path or a drive/UNC root.</summary>
    AbsolutePath = 2,

    /// <summary>More entries than any legitimate driver package contains.</summary>
    TooManyEntries = 3,

    /// <summary>Expanded content exceeded the ceiling. The classic zip bomb.</summary>
    TooLarge = 4,

    /// <summary>The archive could not be read as a ZIP at all.</summary>
    Malformed = 5,

    /// <summary>The INF the task named is not in the archive.</summary>
    InfNotFound = 6,
}

/// <param name="InfPath">Full path to the named INF, once extracted. Null unless <see cref="DriverArchiveResult.Ok"/>.</param>
/// <param name="EntryCount">How many entries were written.</param>
public sealed record DriverArchiveOutcome(
    DriverArchiveResult Result, string? InfPath, int EntryCount, long ExpandedBytes, string? Detail)
{
    public bool Succeeded => Result == DriverArchiveResult.Ok;
}

/// <summary>
/// Extracts a driver package archive into a directory, refusing anything that tries
/// to escape it or exhaust the disk.
/// </summary>
/// <remarks>
/// <para>
/// This exists because a driver package is a folder, not a file. The software path
/// installs a single MSI and never unpacks anything; unpacking is the new attack
/// surface this capability introduces, so it gets its own reviewed component in
/// OS-agnostic code where it can be tested directly rather than being an
/// implementation detail of a Windows class nobody can exercise.
/// </para>
/// <para>
/// <b>Nothing extracted here is ever executed.</b> The files are handed to Windows as
/// data -- an INF and its catalogue and payload -- and Windows decides what to do with
/// them. There is no code path in this agent that runs a binary out of an archive.
/// </para>
/// <para>
/// Every check is applied to the resolved destination path, not the entry name.
/// Prefix-matching a raw name is how path-traversal defences are usually defeated:
/// <c>..%2f</c>, mixed separators, and a sibling directory whose name merely starts
/// with the target's all slip through a naive string comparison.
/// </para>
/// </remarks>
public static class DriverArchive
{
    /// <summary>Far above any real driver package, which is typically tens of files.</summary>
    public const int MaxEntries = 512;

    /// <summary>Expanded ceiling. The archive itself is capped server-side at 256 MB.</summary>
    public const long MaxExpandedBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Extracts <paramref name="archivePath"/> into <paramref name="destinationDirectory"/>.
    /// </summary>
    /// <param name="infFileName">
    /// The INF the task named. A bare file name; it is located among the extracted
    /// entries rather than joined to a caller-supplied path.
    /// </param>
    public static DriverArchiveOutcome Extract(
        string archivePath, string destinationDirectory, string infFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(infFileName);

        Directory.CreateDirectory(destinationDirectory);

        // The comparison root. Trailing separator matters: without it, "C:\a\bc"
        // starts with "C:\a\b" and an escape to a sibling directory reads as safe.
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory))
            + Path.DirectorySeparatorChar;

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(archivePath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return new DriverArchiveOutcome(
                DriverArchiveResult.Malformed, null, 0, 0, "The package is not a readable archive.");
        }

        using (archive)
        {
            if (archive.Entries.Count > MaxEntries)
            {
                return new DriverArchiveOutcome(
                    DriverArchiveResult.TooManyEntries, null, archive.Entries.Count, 0,
                    $"The package contains {archive.Entries.Count} entries; the limit is {MaxEntries}.");
            }

            var written = 0;
            long expanded = 0;

            foreach (var entry in archive.Entries)
            {
                // A directory entry: nothing to write, and its path is created
                // implicitly by the files beneath it.
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                if (Path.IsPathRooted(entry.FullName) || HasDriveOrUncRoot(entry.FullName))
                {
                    return new DriverArchiveOutcome(
                        DriverArchiveResult.AbsolutePath, null, written, expanded,
                        $"Entry '{entry.FullName}' carries an absolute path.");
                }

                var destination = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));

                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    return new DriverArchiveOutcome(
                        DriverArchiveResult.PathTraversal, null, written, expanded,
                        $"Entry '{entry.FullName}' resolves outside the extraction directory.");
                }

                // Checked against the declared length before writing, so a bomb is
                // refused rather than discovered once the disk is full. The written
                // total is checked again below against what actually landed.
                if (expanded + entry.Length > MaxExpandedBytes)
                {
                    return new DriverArchiveOutcome(
                        DriverArchiveResult.TooLarge, null, written, expanded,
                        "The package expands beyond the maximum allowed size.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                try
                {
                    // Not ExtractToFile: overwrite:false plus an explicit CreateNew
                    // means a duplicated entry name cannot overwrite something already
                    // written, and a pre-existing file in the directory is never
                    // silently replaced.
                    using var source = entry.Open();
                    using var target = new FileStream(
                        destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);

                    source.CopyTo(target);
                    expanded += target.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new DriverArchiveOutcome(
                        DriverArchiveResult.Malformed, null, written, expanded,
                        $"Entry '{entry.FullName}' could not be written.");
                }

                if (expanded > MaxExpandedBytes)
                {
                    return new DriverArchiveOutcome(
                        DriverArchiveResult.TooLarge, null, written, expanded,
                        "The package expands beyond the maximum allowed size.");
                }

                written++;
            }

            var infPath = FindInf(destinationDirectory, infFileName);

            return infPath is null
                ? new DriverArchiveOutcome(
                    DriverArchiveResult.InfNotFound, null, written, expanded,
                    $"The package does not contain '{infFileName}'.")
                : new DriverArchiveOutcome(DriverArchiveResult.Ok, infPath, written, expanded, null);
        }
    }

    /// <summary>
    /// Locates the named INF among the extracted files.
    /// </summary>
    /// <remarks>
    /// Matched on file name within the extraction directory rather than by joining a
    /// path, so the name cannot select a file elsewhere however it is spelled. A
    /// package that ships its INF in a subdirectory is normal and is handled; a name
    /// that matches nothing is refused rather than guessed at.
    /// </remarks>
    private static string? FindInf(string destinationDirectory, string infFileName)
    {
        var name = Path.GetFileName(infFileName);

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(destinationDirectory, "*.inf", SearchOption.AllDirectories)
            .FirstOrDefault(f => string.Equals(
                Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Catches roots that <see cref="Path.IsPathRooted(string)"/> misses on the
    /// platform the tests run on, so the check means the same thing everywhere.
    /// </summary>
    private static bool HasDriveOrUncRoot(string entryName) =>
        entryName.StartsWith("//", StringComparison.Ordinal)
        || entryName.StartsWith(@"\\", StringComparison.Ordinal)
        || (entryName.Length >= 2 && entryName[1] == ':');
}
