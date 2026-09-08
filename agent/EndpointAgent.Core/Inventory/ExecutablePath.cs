namespace EndpointAgent.Core.Inventory;

/// <summary>
/// Turns the many shapes an executable path arrives in into one comparable form,
/// or refuses it.
/// </summary>
/// <remarks>
/// <para>
/// Discovery reads paths from places that are not careful: a registry default
/// value, a shortcut's target, a process's image path. They arrive quoted,
/// padded, with arguments appended, with environment variables unexpanded, or
/// simply malformed. Two sources naming one executable differently would become
/// two applications, so normalisation is an identity concern, not tidiness.
/// </para>
/// <para>
/// <b>Nothing here executes anything.</b> A path is data. It is never passed to a
/// shell, never launched, and never used to decide that something may run --
/// which is why refusing a malformed path costs only one piece of evidence.
/// </para>
/// </remarks>
public static class ExecutablePath
{
    /// <summary>
    /// Long enough for any real path, short enough that a hostile registry value
    /// cannot become an allocation.
    /// </summary>
    private const int MaxLength = 512;

    /// <summary>
    /// The comparable form of an executable path, or null when it is not one.
    /// </summary>
    /// <remarks>
    /// Accepts only an absolute local path to a file: a relative path, a UNC
    /// share, a device path or a bare file name is not something a discovery
    /// source can reason about, and guessing what it meant is how a wrong
    /// application ends up in an inventory.
    /// </remarks>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxLength * 2)
        {
            return null;
        }

        var value = raw.Trim();

        // A quoted path may be followed by arguments; an unquoted one may not be
        // split safely, so only the quoted form is unwrapped.
        if (value.StartsWith('"'))
        {
            var closing = value.IndexOf('"', 1);
            if (closing <= 1)
            {
                return null;
            }

            value = value[1..closing];
        }

        value = value.Trim();

        if (value.Length is 0 or > MaxLength)
        {
            return null;
        }

        // Absolute local path only: "C:\...". A UNC path starts "\\", a device
        // path "\\?\", and both name places discovery does not go.
        if (value.Length < 4
            || !char.IsAsciiLetter(value[0])
            || value[1] != ':'
            || (value[2] != '\\' && value[2] != '/'))
        {
            return null;
        }

        if (value.Contains("..", StringComparison.Ordinal))
        {
            // A traversal in a path that was read rather than constructed is not
            // something to canonicalise; it is something to distrust.
            return null;
        }

        if (value.IndexOfAny(['\0', '\r', '\n', '*', '?', '<', '>', '|']) >= 0)
        {
            return null;
        }

        return value.Replace('/', '\\').TrimEnd('\\');
    }

    /// <summary>The directory a normalized executable path sits in, or null.</summary>
    public static string? DirectoryOf(string? normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return null;
        }

        try
        {
            var directory = Path.GetDirectoryName(normalizedPath);
            return string.IsNullOrWhiteSpace(directory) ? null : directory;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether an executable sits inside a directory.
    /// </summary>
    /// <remarks>
    /// Compared with a trailing separator so a directory boundary is respected:
    /// <c>C:\Program Files\Foo</c> must not claim
    /// <c>C:\Program Files\FooBar\app.exe</c>, which a plain prefix test would.
    /// The same rule the process matcher applies, for the same reason.
    /// </remarks>
    public static bool IsUnder(string? executablePath, string? directory)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var prefix = directory.Trim().Trim('"').TrimEnd('\\', '/') + "\\";
        return executablePath.Trim().Trim('"').StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
