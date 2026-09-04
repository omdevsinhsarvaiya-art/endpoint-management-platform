namespace EndpointPlatform.Domain.Software;

/// <summary>
/// Whether an application's recorded install directory is usable for Force Stop.
/// </summary>
/// <remarks>
/// <para>
/// A pre-flight check, not the matching rule. The authority on which processes
/// belong to an application is the agent's <c>ApplicationProcessMatcher</c>,
/// because matching has to happen against live process state at the moment of
/// termination. This exists so the server can answer "Force Stop is unavailable
/// for this application" immediately, instead of queueing a task that the
/// endpoint is certain to refuse.
/// </para>
/// <para>
/// The forbidden-root list is deliberately mirrored on both sides rather than
/// shared: <c>Domain_references_nothing_in_this_solution</c> is an enforced
/// architecture rule, so the server cannot reference the agent's copy. Both sides
/// only ever <em>reject</em>, so the duplication cannot fail open -- the worst a
/// divergence can do is queue a task the agent then declines, which is the
/// behaviour without this check at all.
/// </para>
/// </remarks>
public static class ApplicationInstallLocation
{
    /// <summary>
    /// Directories that are shared or system-owned, and so cannot identify one
    /// application's processes.
    /// </summary>
    private static readonly string[] ForbiddenRoots =
    [
        @"C:\",
        @"C:\Windows",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\Users",
        @"C:\ProgramData",
    ];

    /// <summary>Whether this directory can identify an application's processes.</summary>
    public static bool IsUsable(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation))
        {
            return false;
        }

        var trimmed = installLocation.Trim().Trim('"').TrimEnd('\\', '/');

        // Absolute local paths only. A relative or UNC value is not something
        // either side can reason about safely.
        if (trimmed.Length < 4 || trimmed[1] != ':' || !char.IsAsciiLetter(trimmed[0]))
        {
            return false;
        }

        return !ForbiddenRoots.Any(
            root => string.Equals(trimmed, root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }
}
