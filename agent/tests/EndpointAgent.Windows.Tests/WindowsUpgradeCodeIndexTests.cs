using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// Mapping a Windows Installer product code to the upgrade code of its product
/// line -- the identity an update keeps.
/// </summary>
/// <remarks>
/// The packed-GUID encoding is the trap: get one field's byte order wrong and the
/// index is internally consistent, never matches a real product code, and every
/// MSI product silently reports no upgrade code. So the encoding is pinned to a
/// known pair from a real package, not only round-tripped.
/// </remarks>
public sealed class WindowsUpgradeCodeIndexTests
{
    /// <summary>The agent's own upgrade code, as declared in Package.wxs and read back from the built MSI.</summary>
    private const string AgentUpgradeCode = "{8F3C1D92-6B74-4A5E-9D21-7C4E8B0F5A63}";

    [Fact]
    public void Unpacks_the_installer_encoding_of_a_known_upgrade_code()
    {
        // Each of the five fields packs differently; this pair exercises all five.
        WindowsUpgradeCodeIndex.Unpack("29D1C3F847B6E5A4D912C7E4B8F0A536").ShouldBe(AgentUpgradeCode);
    }

    [Fact]
    public void Unpacking_is_case_insensitive_in_and_canonical_out()
    {
        WindowsUpgradeCodeIndex.Unpack("29d1c3f847b6e5a4d912c7e4b8f0a536").ShouldBe(AgentUpgradeCode);
    }

    /// <summary>Windows keeps stray values under these keys; they must read as nothing.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("29D1C3F8")]
    [InlineData("29D1C3F847B6E5A4D912C7E4B8F0A53")]
    [InlineData("29D1C3F847B6E5A4D912C7E4B8F0A5366")]
    [InlineData("29D1C3F847B6E5A4D912C7E4B8F0A53G")]
    [InlineData("{8F3C1D92-6B74-4A5E-9D21-7C4E8B0F5A63}")]
    public void Anything_that_is_not_thirty_two_hex_characters_is_not_a_packed_guid(string? packed)
    {
        WindowsUpgradeCodeIndex.Unpack(packed).ShouldBeNull();
    }

    /// <summary>
    /// Against this machine's real Installer registry: the agent product installed
    /// here belongs to the agent's upgrade line. If the encoding were wrong in a
    /// way the fixed pair above did not catch, this is where it would show.
    /// </summary>
    [Fact]
    public void Resolves_the_installed_agents_product_code_to_its_upgrade_code()
    {
        var productCode = InstalledAgentProductCode();
        if (productCode is null)
        {
            // Not an installed-agent machine (CI). The encoding test above still
            // holds; only the live lookup is unavailable.
            return;
        }

        var index = new WindowsUpgradeCodeIndex(NullLogger<WindowsUpgradeCodeIndex>.Instance);

        index.For(productCode).ShouldBe(AgentUpgradeCode);
    }

    [Fact]
    public void An_unknown_product_code_has_no_upgrade_code()
    {
        var index = new WindowsUpgradeCodeIndex(NullLogger<WindowsUpgradeCodeIndex>.Instance);

        index.For("{00000000-0000-0000-0000-000000000000}").ShouldBeNull();
        index.For(null).ShouldBeNull();
        index.For("   ").ShouldBeNull();
    }

    /// <summary>The index is rebuilt per collection, like the install location resolver.</summary>
    [Fact]
    public void A_new_collection_rereads_the_machine()
    {
        var index = new WindowsUpgradeCodeIndex(NullLogger<WindowsUpgradeCodeIndex>.Instance);
        index.For("{00000000-0000-0000-0000-000000000000}");

        Should.NotThrow(() => index.BeginCollection());
        index.For("{00000000-0000-0000-0000-000000000000}").ShouldBeNull();
    }

    /// <summary>The product code of the Endpoint Platform Agent installed on this machine, if any.</summary>
    private static string? InstalledAgentProductCode()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        if (uninstall is null)
        {
            return null;
        }

        foreach (var name in uninstall.GetSubKeyNames())
        {
            using var entry = uninstall.OpenSubKey(name);
            if (entry?.GetValue("DisplayName") as string == "Endpoint Platform Agent" && Guid.TryParse(name, out _))
            {
                return name;
            }
        }

        return null;
    }
}
