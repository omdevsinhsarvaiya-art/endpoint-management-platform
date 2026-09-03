using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Inventory;

/// <summary>Where Windows recorded an installed application.</summary>
public enum SoftwareScope
{
    /// <summary>An all-users install, under HKLM.</summary>
    Machine = 0,

    /// <summary>An install belonging to one user's profile hive.</summary>
    User = 1,
}

/// <summary>
/// One application as a discovery source found it, before normalization.
/// </summary>
/// <remarks>
/// Deliberately a plain record with no Windows types: it is what the platform
/// layer produces and what the tests construct, so the normalization rules below
/// can be exercised with fixtures instead of a real machine's registry.
/// </remarks>
/// <param name="RegistryView">
/// <c>x64</c>, <c>x86</c>, or null. Where the entry was found, not what the
/// binary is -- see <see cref="InventorySoftware"/>.
/// </param>
public sealed record DiscoveredSoftware(
    string? Name,
    string? Version = null,
    string? Publisher = null,
    string? InstallDate = null,
    string? InstallLocation = null,
    string? RegistryView = null,
    SoftwareScope Scope = SoftwareScope.Machine,
    string? InstalledForUser = null,
    string? ProductCode = null);

/// <summary>
/// Turns everything the discovery sources found into the list the server will
/// accept: de-duplicated, clamped to the wire limits, and bounded.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and separate from the Windows collector on purpose. Enumerating the
/// registry needs a real machine; deciding what counts as the same application
/// does not, and that decision is where the bugs live. Everything here is
/// exercised by fixtures.
/// </para>
/// <para>
/// <b>The clamping is a correctness requirement, not tidiness.</b> The Agent API
/// validates the whole inventory report and rejects it outright -- security
/// posture, BitLocker and drivers included -- if any software field is over
/// length or the list is too long. Truncating here means a machine with an
/// unusual application still reports everything else; letting it through would
/// cost the entire report.
/// </para>
/// </remarks>
public static class SoftwareInventoryNormalizer
{
    // Mirrors the Agent API's own validation. Kept slightly under the server's
    // 8192 so a fleet machine with many profiles degrades by dropping the tail of
    // an already-implausible list rather than losing its whole inventory report.
    public const int MaxEntries = 8000;

    private const int MaxName = 384;
    private const int MaxVersion = 128;
    private const int MaxPublisher = 256;
    private const int MaxInstallLocation = 512;
    private const int MaxInstallDate = 32;
    private const int MaxScope = 16;
    private const int MaxUser = 256;
    private const int MaxProductCode = 64;

    /// <summary>
    /// ASCII Unit Separator, joining the identity fields.
    /// </summary>
    /// <remarks>
    /// A character no DisplayName, publisher or account name can contain, so
    /// ("A", "B|C") and ("A|B", "C") cannot collide into one identity and quietly
    /// hide an application.
    /// </remarks>
    private const char IdentitySeparator = (char)0x1F;

    /// <summary>
    /// Collapses duplicates and produces the reportable list, ordered by name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The identity of an installation is (name, version, publisher, scope, user).
    /// Name alone is too coarse -- two publishers ship a "Setup" -- and including
    /// version keeps a genuine side-by-side install visible rather than silently
    /// picking one.
    /// </para>
    /// <para>
    /// Scope and user are part of the key because the same product installed for
    /// two people is two installations, not a duplicate: uninstalling one leaves
    /// the other running. Machine-wide and per-user copies of the same product are
    /// likewise both real. What this does collapse is the same entry seen twice
    /// through different registry views, which is the actual duplication Windows
    /// produces.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<InventorySoftware> Normalize(IEnumerable<DiscoveredSoftware> discovered)
    {
        ArgumentNullException.ThrowIfNull(discovered);

        var byIdentity = new Dictionary<string, InventorySoftware>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in discovered)
        {
            var name = Clamp(raw.Name, MaxName);
            if (name is null)
            {
                continue; // Updates and patches have no DisplayName.
            }

            var version = Clamp(raw.Version, MaxVersion);
            var publisher = Clamp(raw.Publisher, MaxPublisher);
            var scope = raw.Scope == SoftwareScope.User ? "User" : "Machine";
            var user = raw.Scope == SoftwareScope.User ? Clamp(raw.InstalledForUser, MaxUser) : null;

            var identity = string.Join(
                IdentitySeparator, name, version ?? string.Empty, publisher ?? string.Empty, scope, user ?? string.Empty);

            if (byIdentity.ContainsKey(identity))
            {
                continue;
            }

            byIdentity.Add(identity, new InventorySoftware(
                name,
                version,
                publisher,
                Clamp(raw.InstallDate, MaxInstallDate),
                Clamp(raw.InstallLocation, MaxInstallLocation),
                Clamp(raw.RegistryView, MaxScope),
                Clamp(scope, MaxScope),
                user,
                Clamp(raw.ProductCode, MaxProductCode)));
        }

        return byIdentity.Values
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Version, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.InstalledForUser, StringComparer.OrdinalIgnoreCase)
            .Take(MaxEntries)
            .ToArray();
    }

    /// <summary>Trims, treats blank as absent, and truncates to the wire limit.</summary>
    private static string? Clamp(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
