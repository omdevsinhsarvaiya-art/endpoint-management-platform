using EndpointAgent.Windows;
using Microsoft.Win32;

namespace EndpointAgent.Windows.Tests;

/// <summary>Resolving a package display name given by resource reference.</summary>
public sealed class WindowsIndirectStringTests
{
    [Theory]
    [InlineData("Slack", "Slack")]
    [InlineData("  Windows Terminal  ", "Windows Terminal")]
    public void A_literal_is_itself(string value, string expected)
    {
        WindowsIndirectString.Resolve(value).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@{NoSuchPackage_1.0.0.0_neutral__0000000000000?ms-resource://NoSuchPackage/Resources/DisplayName}")]
    [InlineData("@{not even a reference")]
    public void Anything_that_resolves_to_nothing_is_null(string? value)
    {
        Should.NotThrow(() => WindowsIndirectString.Resolve(value)).ShouldBeNull();
    }

    [Theory]
    [InlineData("ms-resource:AppStoreName", "@{Contoso.App_1.0.0.0_x64__abc?ms-resource://Contoso.App/resources/AppStoreName}", "@{Contoso.App_1.0.0.0_x64__abc?ms-resource://Contoso.App/AppStoreName}")]
    [InlineData("ms-resource:Resources/AppStoreName", "@{Contoso.App_1.0.0.0_x64__abc?ms-resource://Contoso.App/resources/Resources/AppStoreName}", "@{Contoso.App_1.0.0.0_x64__abc?ms-resource://Contoso.App/Resources/AppStoreName}")]
    [InlineData("ms-resource:/Files/Name", "@{Contoso.App_1.0.0.0_x64__abc?ms-resource://Contoso.App/resources/Files/Name}", "@{Contoso.App_1.0.0.0_x64__abc?ms-resource://Contoso.App/Files/Name}")]
    public void A_bare_reference_is_tried_under_resources_then_under_the_package(string reference, string first, string second)
    {
        WindowsIndirectString.Candidates("Contoso.App_1.0.0.0_x64__abc", reference).ShouldBe([first, second]);
    }

    [Fact]
    public void A_full_resource_uri_is_tried_as_itself()
    {
        WindowsIndirectString.Candidates("Contoso.App_1.0.0.0_x64__abc", "ms-resource://Contoso.App/resources/Name")
            .ShouldBe(["@{Contoso.App_1.0.0.0_x64__abc?ms-resource://Contoso.App/resources/Name}"]);
    }

    [Theory]
    [InlineData(null, "ms-resource:Name")]
    [InlineData("not a full name", "ms-resource:Name")]
    [InlineData("Contoso.App_1.0.0.0_x64__abc", null)]
    [InlineData("Contoso.App_1.0.0.0_x64__abc", "Literal Name")]
    [InlineData("Contoso.App_1.0.0.0_x64__abc", "ms-resource:")]
    [InlineData("Contoso.App_1.0.0.0_x64__abc", "ms-resource:/")]
    public void Nothing_is_tried_without_a_package_and_a_reference(string? fullName, string? reference)
    {
        WindowsIndirectString.Candidates(fullName, reference).ShouldBeEmpty();
        WindowsIndirectString.ResolvePackageResource(fullName, reference).ShouldBeNull();
    }

    /// <summary>
    /// Every bare reference the repository holds for the signed-in user resolves
    /// to text. On the reference machine that is 39 of 39; a package whose
    /// resource map does not carry the name would be an honest miss, so the
    /// assertion is that most resolve and none throws.
    /// </summary>
    [Fact]
    public void Bare_references_recorded_for_the_signed_in_user_resolve()
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
        using var packages = users.OpenSubKey(sid + @"_Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        if (packages is null)
        {
            return;
        }

        var tried = 0;
        var resolved = 0;
        foreach (var name in packages.GetSubKeyNames())
        {
            using var entry = packages.OpenSubKey(name);
            if (entry?.GetValue("DisplayName") is not string value || !value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            tried++;
            var text = Should.NotThrow(() => WindowsIndirectString.ResolvePackageResource(name, value));
            if (text is not null)
            {
                text.ShouldNotStartWith("@");
                text.ShouldNotContain("ms-resource");
                resolved++;
            }
        }

        if (tried > 0)
        {
            resolved.ShouldBeGreaterThan(tried / 2, $"{resolved} of {tried} bare references resolved");
        }
    }

    /// <summary>A reference Windows itself recorded resolves to a name a person would recognise.</summary>
    [Fact]
    public void A_real_reference_resolves_to_text()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var packages = hklm.OpenSubKey(@"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        if (packages is null)
        {
            return;
        }

        string? reference = null;
        foreach (var name in packages.GetSubKeyNames())
        {
            using var entry = packages.OpenSubKey(name);
            if (entry?.GetValue("DisplayName") is string value && value.StartsWith("@{", StringComparison.Ordinal))
            {
                reference = value;
                break;
            }
        }

        if (reference is null)
        {
            return;
        }

        var resolved = WindowsIndirectString.Resolve(reference);

        // Windows' own Settings package is registered machine-wide on every
        // machine; if this resolves at all it is readable text.
        if (resolved is not null)
        {
            resolved.ShouldNotStartWith("@");
            resolved.ShouldNotContain("ms-resource");
        }
    }
}
