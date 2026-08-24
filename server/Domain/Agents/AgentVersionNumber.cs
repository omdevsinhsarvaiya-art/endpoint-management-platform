namespace EndpointPlatform.Domain.Agents;

/// <summary>
/// Deterministic, numeric agent-version comparison.
/// </summary>
/// <remarks>
/// Versions are compared as three numbers, never as strings: lexicographic
/// comparison thinks <c>1.0.9 &gt; 1.0.10</c>, and an update mechanism that
/// inherits that bug either skips real updates or downgrades. Exactly three
/// numeric parts are required — this is the platform's own version scheme, so
/// tolerating other shapes would only let a typo order itself somewhere
/// surprising.
/// </remarks>
public static class AgentVersionNumber
{
    public static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0, 0);

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        var parts = trimmed.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        // Explicit digit check: Version.TryParse would accept "1.2.-3" nowhere,
        // but int.TryParse accepts leading '+' and whitespace, which are not
        // version syntax.
        foreach (var part in parts)
        {
            if (part.Length == 0 || part.Length > 9 || !part.All(char.IsAsciiDigit))
            {
                return false;
            }
        }

        version = new Version(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
        return true;
    }

    /// <summary>Validates and returns the canonical form, or throws.</summary>
    public static string Normalize(string value)
    {
        if (!TryParse(value, out var version))
        {
            throw new ArgumentException(
                $"'{value}' is not a three-part numeric version (e.g. 1.1.0).", nameof(value));
        }

        return $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is strictly newer than
    /// <paramref name="installed"/>. Unparseable input is never "newer" — an
    /// update decision based on a version nobody can read must fail closed.
    /// </summary>
    public static bool IsNewer(string? candidate, string? installed)
    {
        if (!TryParse(candidate, out var candidateVersion) || !TryParse(installed, out var installedVersion))
        {
            return false;
        }

        return candidateVersion > installedVersion;
    }
}
