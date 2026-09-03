namespace EndpointPlatform.Domain.Software;

/// <summary>
/// Compares the version strings real Windows applications actually report.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <see cref="Agents.AgentVersionNumber"/>, which
/// requires exactly three numeric parts because it governs the platform's own
/// agent versioning and can insist on a scheme. Installed software cannot: the
/// fleet's own inventory holds <c>7.1.5 (43453)</c>, <c>152.0.7977.65</c>,
/// <c>24.09</c>, <c>10.1.26100.8249</c> and <c>1.1</c>. Feeding those to a
/// three-part parser returns "not comparable" for most of the estate, and an
/// eligibility engine that cannot compare anything either skips every device or
/// reinstalls every device.
/// </para>
/// <para>
/// So this parses one to four leading numeric components and ignores any
/// trailing build tag. It is a comparison, not a validator: it never rejects a
/// version, it only reports whether two can be ordered.
/// </para>
/// <para>
/// <b>Fails closed.</b> When two versions cannot be ordered the answer is null,
/// never a guess. The caller treats that as "do not deploy" and says so, because
/// the alternative is installing over an unknown version — which is a silent
/// downgrade whenever the guess is wrong.
/// </para>
/// </remarks>
public static class SoftwareVersion
{
    private const int MaxComponents = 4;

    /// <summary>
    /// Orders two versions: negative if <paramref name="left"/> is older, zero if
    /// equal, positive if newer. Null when they cannot be compared.
    /// </summary>
    public static int? Compare(string? left, string? right)
    {
        if (!TryParse(left, out var a) || !TryParse(right, out var b))
        {
            return null;
        }

        for (var i = 0; i < MaxComponents; i++)
        {
            // A missing component is zero, so 1.5 and 1.5.0 are the same version
            // rather than an update that would reinstall on every deployment.
            var difference = a[i].CompareTo(b[i]);
            if (difference != 0)
            {
                return difference;
            }
        }

        return 0;
    }

    /// <summary>Whether two version strings denote the same version.</summary>
    /// <remarks>
    /// Falls back to an exact text match so that two identical unparseable
    /// versions still count as equal. A device reporting exactly what the package
    /// declares is installed, whatever shape the string has, and reinstalling it
    /// would be busywork on a machine that is already correct.
    /// </remarks>
    public static bool AreSame(string? left, string? right)
    {
        if (Compare(left, right) is { } ordering)
        {
            return ordering == 0;
        }

        return !string.IsNullOrWhiteSpace(left)
            && string.Equals(left.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the leading numeric components, ignoring any trailing build tag.
    /// </summary>
    /// <remarks>
    /// <c>7.1.5 (43453)</c> parses as 7.1.5: the parenthesised build is Zoom's
    /// own annotation and is not part of the ordering. Parsing stops at the first
    /// component that is not a plain number, so <c>1.0-beta</c> is 1.0 rather
    /// than unreadable — a prerelease tag is not something this can order, and
    /// pretending otherwise would be worse than ignoring it.
    /// </remarks>
    private static bool TryParse(string? value, out int[] components)
    {
        components = new int[MaxComponents];

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parsed = 0;
        foreach (var raw in value.Trim().Split('.'))
        {
            if (parsed == MaxComponents)
            {
                break;
            }

            var token = raw.AsSpan();
            var digits = 0;
            while (digits < token.Length && char.IsAsciiDigit(token[digits]))
            {
                digits++;
            }

            // Nothing numeric at the head of this component: stop rather than
            // skip, so "1.x.5" is 1 and never accidentally 1.5.
            if (digits == 0)
            {
                break;
            }

            // Absurdly long runs are not versions; refuse rather than overflow.
            if (digits > 9)
            {
                return false;
            }

            components[parsed++] = int.Parse(token[..digits]);

            // A component with trailing non-digits ends the version: the rest is
            // a build tag, not a further component.
            if (digits != token.Length)
            {
                break;
            }
        }

        return parsed > 0;
    }
}
