namespace EndpointAgent.Core.Inventory;

/// <summary>One process the endpoint reported as running.</summary>
public sealed record RunningProcess(int ProcessId, string Name, string? ExecutablePath);

/// <summary>A process this application owns, and may therefore be asked to stop.</summary>
public sealed record MatchedProcess(int ProcessId, string ImageName);

/// <summary>
/// Works out which running processes belong to an installed application.
/// </summary>
/// <remarks>
/// <para>
/// <b>Matched by install path, never by name.</b> A display name and an image
/// name are different things and the gap is not guessable: "Microsoft Visual
/// Studio Code" runs as <c>Code.exe</c>, "Google Chrome" as <c>chrome.exe</c>,
/// "Zoom Workplace" as <c>Zoom.exe</c>. Deriving one from the other would mean
/// inventing a name table that is wrong for every application nobody thought of,
/// and being wrong here means terminating somebody else's process.
/// </para>
/// <para>
/// So the only evidence used is what the endpoint itself reported: the
/// application's <c>InstallLocation</c> and each process's executable path. A
/// process counts as the application's when its executable lives inside the
/// directory the application was installed into. That is a fact about the
/// machine, not an inference about naming.
/// </para>
/// <para>
/// When there is no such evidence -- no install location, or nothing running
/// under it -- the answer is "no reliable mapping" and the console says so. An
/// application that cannot be resolved is not force-stoppable, and offering the
/// action anyway would be offering a guess.
/// </para>
/// </remarks>
public static class ApplicationProcessMatcher
{
    /// <summary>
    /// Directories an install location may never resolve to.
    /// </summary>
    /// <remarks>
    /// A bogus or over-broad <c>InstallLocation</c> -- some installers write
    /// <c>C:\</c> or the bare Program Files root -- would otherwise match a large
    /// share of everything running, including Windows itself. Refusing these is
    /// what stops a Force Stop on one badly-registered application becoming a
    /// request to terminate the operating system.
    /// </remarks>
    private static readonly string[] ForbiddenRoots =
    [
        @"C:\",
        @"C:\Windows",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\Users",
        @"C:\ProgramData",
    ];

    /// <summary>
    /// The processes belonging to an application, or empty when it cannot be
    /// resolved safely.
    /// </summary>
    /// <param name="installLocation">
    /// The application's install directory as inventory reported it. Null or
    /// unusable means no mapping, which is a supported answer.
    /// </param>
    public static IReadOnlyList<MatchedProcess> Match(
        string? installLocation, IEnumerable<RunningProcess> running)
    {
        ArgumentNullException.ThrowIfNull(running);

        var root = NormalizeRoot(installLocation);
        if (root is null)
        {
            return [];
        }

        var matches = new List<MatchedProcess>();
        var seen = new HashSet<int>();

        foreach (var process in running)
        {
            if (string.IsNullOrWhiteSpace(process.ExecutablePath) || process.ProcessId <= 4)
            {
                // No path is no evidence. PIDs 0 and 4 are System Idle and System
                // and are never anyone's application.
                continue;
            }

            if (!IsUnder(process.ExecutablePath, root))
            {
                continue;
            }

            if (seen.Add(process.ProcessId))
            {
                matches.Add(new MatchedProcess(process.ProcessId, process.Name));
            }
        }

        return matches;
    }

    /// <summary>Whether an application can be force-stopped at all.</summary>
    public static bool CanResolve(string? installLocation) => NormalizeRoot(installLocation) is not null;

    /// <summary>
    /// The install directory in comparable form, or null when it is unusable.
    /// </summary>
    private static string? NormalizeRoot(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation))
        {
            return null;
        }

        var trimmed = installLocation.Trim().Trim('"').TrimEnd('\\', '/');

        // Must be an absolute local path. A relative or UNC value is not
        // something this can reason about safely.
        if (trimmed.Length < 4 || trimmed[1] != ':' || !char.IsAsciiLetter(trimmed[0]))
        {
            return null;
        }

        foreach (var forbidden in ForbiddenRoots)
        {
            if (string.Equals(trimmed, forbidden.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Whether an executable sits inside a directory.
    /// </summary>
    /// <remarks>
    /// Compared with a trailing separator so a directory boundary is respected:
    /// <c>C:\Program Files\Foo</c> must not claim
    /// <c>C:\Program Files\FooBar\app.exe</c>, which a plain prefix test would.
    /// </remarks>
    private static bool IsUnder(string executablePath, string root)
    {
        var path = executablePath.Trim().Trim('"');
        var prefix = root + "\\";

        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
