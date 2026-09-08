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
/// The nine leading fields are the ones the inventory has always carried; the
/// rest arrived with application discovery and are optional, so every existing
/// caller and fixture is unchanged.
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
    string? ProductCode = null,
    string? IdentityKind = null,
    string? StableKey = null,
    string? VersionKey = null,
    string? Confidence = null,
    string? Category = null,
    string? PackageFamilyName = null,
    string? PackageFullName = null,
    string? UpgradeCode = null,
    string? ExecutablePath = null,
    string? SignerSubject = null,
    string? SignatureStatus = null,
    IReadOnlyList<InventorySoftwareEvidence>? Evidence = null);

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

    // The application-discovery fields, at the limits InventorySoftware documents.
    private const int MaxIdentityKind = InventorySoftware.MaxIdentityKind;
    private const int MaxStableKey = InventorySoftware.MaxStableKey;
    private const int MaxVersionKey = InventorySoftware.MaxVersionKey;
    private const int MaxConfidence = InventorySoftware.MaxConfidence;
    private const int MaxCategory = InventorySoftware.MaxCategory;
    private const int MaxPackageName = InventorySoftware.MaxPackageName;
    private const int MaxUpgradeCode = InventorySoftware.MaxUpgradeCode;
    private const int MaxExecutablePath = InventorySoftware.MaxExecutablePath;
    private const int MaxSignerSubject = InventorySoftware.MaxSignerSubject;
    private const int MaxSignatureStatus = InventorySoftware.MaxSignatureStatus;

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
    /// Produces the wire-ready list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The identity of an installation is (name, version, publisher, scope,
    /// account). Scope and account are part of it because the same product
    /// installed for two people is two installations - uninstalling one leaves
    /// the other running - and folding them would hide that. The same product
    /// under both registry views of one machine-wide install, however, is one
    /// installation, and the first occurrence wins.
    /// </para>
    /// <para>
    /// Comparison is case-insensitive, matching what Windows does for registry
    /// key names and what an administrator would expect. The clamp-then-compare
    /// order matters: two names that differ only past the limit are the same
    /// after truncation, and treating them as distinct would produce two rows
    /// with identical text -- exactly the duplicate-looking output the rule
    /// exists to prevent. The tests assert the order, not the intent.
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
                Clamp(raw.ProductCode, MaxProductCode),
                Clamp(raw.IdentityKind, MaxIdentityKind),
                Clamp(raw.StableKey, MaxStableKey),
                Clamp(raw.VersionKey, MaxVersionKey),
                Clamp(raw.Confidence, MaxConfidence),
                Clamp(raw.Category, MaxCategory),
                Clamp(raw.PackageFamilyName, MaxPackageName),
                Clamp(raw.PackageFullName, MaxPackageName),
                Clamp(raw.UpgradeCode, MaxUpgradeCode),
                Clamp(raw.ExecutablePath, MaxExecutablePath),
                Clamp(raw.SignerSubject, MaxSignerSubject),
                Clamp(raw.SignatureStatus, MaxSignatureStatus),
                ClampEvidence(raw.Evidence)));
        }

        return byIdentity.Values
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Version, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.InstalledForUser, StringComparer.OrdinalIgnoreCase)
            .Take(MaxEntries)
            .ToArray();
    }

    /// <summary>Trims and truncates; null for anything blank.</summary>
    private static string? Clamp(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    /// <summary>
    /// The evidence list, bounded and clamped, or null when there is none. An
    /// entry with no source is not evidence of anything and is dropped.
    /// </summary>
    private static IReadOnlyList<InventorySoftwareEvidence>? ClampEvidence(IReadOnlyList<InventorySoftwareEvidence>? evidence)
    {
        if (evidence is null || evidence.Count == 0)
        {
            return null;
        }

        var kept = new List<InventorySoftwareEvidence>(Math.Min(evidence.Count, InventorySoftware.MaxEvidence));

        foreach (var item in evidence)
        {
            if (item is null || Clamp(item.Source, InventorySoftwareEvidence.MaxSource) is not { } source)
            {
                continue;
            }

            kept.Add(new InventorySoftwareEvidence(
                source,
                Clamp(item.Name, InventorySoftwareEvidence.MaxName),
                Clamp(item.Detail, InventorySoftwareEvidence.MaxDetail)));

            if (kept.Count >= InventorySoftware.MaxEvidence)
            {
                break;
            }
        }

        return kept.Count == 0 ? null : kept;
    }
}
