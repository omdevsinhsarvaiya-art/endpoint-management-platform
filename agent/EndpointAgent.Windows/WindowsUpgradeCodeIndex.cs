using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EndpointAgent.Windows;

/// <summary>
/// Maps a Windows Installer product code to the upgrade code of the product line
/// it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// The upgrade code is the vendor's own statement that two releases are the same
/// product: it is chosen once and kept across versions, while the product code
/// changes with every release. That makes it the identifier an application should
/// be known by, and the difference between "the agent updated from 1.7.0 to 1.8.0"
/// and "two agents are installed".
/// </para>
/// <para>
/// It is not in the uninstall key, and asking Windows Installer for it product by
/// product would mean one <c>MsiGetProductInfo</c> call each. Windows already
/// keeps the reverse mapping in the registry -- one key per upgrade code, holding
/// one value per product in that line -- so the whole index is read once, in a
/// single pass, and looked up from memory.
/// </para>
/// <para>
/// Both sides of that mapping are stored as <em>packed</em> GUIDs, a Windows
/// Installer encoding with no separators and several fields byte-reversed. See
/// <see cref="Unpack"/>: getting this wrong silently produces an index that never
/// matches anything, which is why it is tested against a known pair rather than
/// only for self-consistency.
/// </para>
/// <para>
/// Read-only, and failure is never fatal: a machine whose Installer registry
/// cannot be read reports products without upgrade codes rather than reporting
/// nothing.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsUpgradeCodeIndex(ILogger<WindowsUpgradeCodeIndex> logger)
{
    private readonly ILogger<WindowsUpgradeCodeIndex> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Where Windows records which products belong to which upgrade code.</summary>
    private const string MachineUpgradeCodes = @"SOFTWARE\Classes\Installer\UpgradeCodes";

    /// <summary>
    /// A machine with more upgrade codes than this is not a machine this index is
    /// for. Bounded so a corrupt or hostile registry cannot cost unbounded memory.
    /// </summary>
    private const int MaxUpgradeCodes = 20_000;

    private Dictionary<string, string>? _productToUpgrade;

    /// <summary>
    /// Discards the index so the next lookup re-reads the machine.
    /// </summary>
    /// <remarks>
    /// Called at the start of each collection, for the same reason the install
    /// location resolver is: this is a singleton, and a cache kept across
    /// collections would age with the service rather than with the machine.
    /// </remarks>
    public void BeginCollection() => _productToUpgrade = null;

    /// <summary>The upgrade code for a product code, or null when there is none.</summary>
    public string? For(string? productCode)
    {
        if (string.IsNullOrWhiteSpace(productCode))
        {
            return null;
        }

        var index = Index();
        return index.TryGetValue(productCode.Trim(), out var upgradeCode) ? upgradeCode : null;
    }

    private Dictionary<string, string> Index()
    {
        if (_productToUpgrade is not null)
        {
            return _productToUpgrade;
        }

        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var upgradeCodes = baseKey.OpenSubKey(MachineUpgradeCodes);

            if (upgradeCodes is not null)
            {
                var seen = 0;

                foreach (var packedUpgradeCode in upgradeCodes.GetSubKeyNames())
                {
                    if (++seen > MaxUpgradeCodes)
                    {
                        _logger.LogWarning(
                            "Stopping upgrade-code indexing at {Max} entries; the rest are not indexed.", MaxUpgradeCodes);
                        break;
                    }

                    var upgradeCode = Unpack(packedUpgradeCode);
                    if (upgradeCode is null)
                    {
                        continue;
                    }

                    try
                    {
                        using var entry = upgradeCodes.OpenSubKey(packedUpgradeCode);
                        if (entry is null)
                        {
                            continue;
                        }

                        // One value per product in this upgrade line, named by the
                        // packed product code. The value's data is not needed.
                        foreach (var packedProductCode in entry.GetValueNames())
                        {
                            if (Unpack(packedProductCode) is { } productCode)
                            {
                                index[productCode] = upgradeCode;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
                    {
                        _logger.LogDebug(ex, "Skipping unreadable upgrade-code entry.");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the Installer upgrade-code registry; products report no upgrade code.");
        }

        _logger.LogDebug("Indexed upgrade codes for {Count} product(s).", index.Count);

        _productToUpgrade = index;
        return index;
    }

    /// <summary>
    /// Turns a Windows Installer packed GUID back into its normal form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The packing is 32 hex characters with the braces and hyphens removed and
    /// five fields rearranged: the first three are byte-reversed as whole fields
    /// (8, 4 and 4 characters), and the last two have each byte's two characters
    /// swapped. So <c>{8F3C1D92-6B74-4A5E-9D21-7C4E8B0F5A63}</c> is stored as
    /// <c>29D1C3F847B6E5A4D912C7E4B8F0A536</c>.
    /// </para>
    /// <para>
    /// Returns null for anything that is not 32 hex characters, which is the
    /// correct answer for the stray values Windows also keeps under these keys.
    /// </para>
    /// </remarks>
    internal static string? Unpack(string? packed)
    {
        if (packed is null || packed.Length != 32 || !packed.All(Uri.IsHexDigit))
        {
            return null;
        }

        Span<char> unpacked = stackalloc char[36];
        var at = 0;

        void Reversed(ReadOnlySpan<char> source, Span<char> target, ref int offset)
        {
            for (var i = source.Length - 1; i >= 0; i--)
            {
                target[offset++] = source[i];
            }
        }

        void ByteSwapped(ReadOnlySpan<char> source, Span<char> target, ref int offset)
        {
            for (var i = 0; i < source.Length; i += 2)
            {
                target[offset++] = source[i + 1];
                target[offset++] = source[i];
            }
        }

        var span = packed.AsSpan();

        Reversed(span[..8], unpacked, ref at);
        unpacked[at++] = '-';
        Reversed(span[8..12], unpacked, ref at);
        unpacked[at++] = '-';
        Reversed(span[12..16], unpacked, ref at);
        unpacked[at++] = '-';
        ByteSwapped(span[16..20], unpacked, ref at);
        unpacked[at++] = '-';
        ByteSwapped(span[20..], unpacked, ref at);

        return string.Concat("{", new string(unpacked).ToUpperInvariant(), "}");
    }
}
