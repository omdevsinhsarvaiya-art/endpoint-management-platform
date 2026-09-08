using System.Text;
using EndpointAgent.Core.Inventory;

namespace EndpointAgent.Core.Tests.Inventory;

/// <summary>
/// Reading a package manifest: the few elements discovery needs, and refusal of
/// everything a hostile or broken file could try.
/// </summary>
public sealed class AppxManifestTests
{
    /// <summary>Slack's manifest, as deployed, trimmed to the parts that matter and one that does not.</summary>
    private const string Slack = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package
          xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
          xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
          xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
          IgnorableNamespaces="uap desktop">
          <Identity
            Name="com.tinyspeck.slackdesktop"
            ProcessorArchitecture="x64"
            Publisher="CN=&quot;Slack Technologies, LLC&quot;, O=&quot;Slack Technologies, LLC&quot;, L=San Francisco, S=California, C=US"
            Version="4.52.155.0" />
          <Properties>
            <DisplayName>Slack</DisplayName>
            <PublisherDisplayName>Slack Technologies Inc.</PublisherDisplayName>
            <Logo>Assets\SlackStoreLogo.png</Logo>
          </Properties>
          <Resources>
            <Resource Language="EN" />
          </Resources>
          <Dependencies>
            <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.17763.0" />
          </Dependencies>
          <Capabilities>
            <rescap:Capability xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities" Name="runFullTrust" />
          </Capabilities>
          <Applications>
            <Application Id="Slack" Executable="app\Slack.exe" EntryPoint="Windows.FullTrustApplication">
              <uap:VisualElements DisplayName="Slack" Description="Slack" BackgroundColor="transparent" Square150x150Logo="Assets\a.png" Square44x44Logo="Assets\b.png" />
            </Application>
          </Applications>
        </Package>
        """;

    [Fact]
    public void Reads_identity_properties_and_the_first_application()
    {
        var manifest = AppxManifest.Parse(Slack).ShouldNotBeNull();

        manifest.Name.ShouldBe("com.tinyspeck.slackdesktop");
        manifest.Publisher.ShouldBe("CN=\"Slack Technologies, LLC\", O=\"Slack Technologies, LLC\", L=San Francisco, S=California, C=US");
        manifest.Version.ShouldBe("4.52.155.0");
        manifest.Architecture.ShouldBe("x64");
        manifest.DisplayName.ShouldBe("Slack");
        manifest.PublisherDisplayName.ShouldBe("Slack Technologies Inc.");
        manifest.IsFramework.ShouldBeFalse();
        manifest.IsResourcePackage.ShouldBeFalse();
        manifest.Executable.ShouldBe(@"app\Slack.exe");
    }

    [Fact]
    public void A_framework_package_says_so()
    {
        var manifest = AppxManifest.Parse("""
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Microsoft.VCLibs.140.00" Publisher="CN=Microsoft Corporation" Version="14.0.33728.0" ProcessorArchitecture="x64" />
              <Properties>
                <Framework>true</Framework>
                <DisplayName>Microsoft Visual C++ 2015 UWP Desktop Runtime Package</DisplayName>
                <PublisherDisplayName>Microsoft Corporation</PublisherDisplayName>
              </Properties>
            </Package>
            """).ShouldNotBeNull();

        manifest.IsFramework.ShouldBeTrue();
        manifest.Executable.ShouldBeNull();
    }

    [Theory]
    [InlineData("""<Properties><ResourcePackage>true</ResourcePackage></Properties>""", "", true)]
    [InlineData("""<Properties><DisplayName>x</DisplayName></Properties>""", """ResourceId="split.language-de" """, true)]
    [InlineData("""<Properties><DisplayName>x</DisplayName></Properties>""", """ResourceId="split.scale-200" """, true)]
    // Windows writes ResourceId="neutral" on ordinary packages (File Explorer, the
    // file picker); that is not a resource package.
    [InlineData("""<Properties><DisplayName>x</DisplayName></Properties>""", """ResourceId="neutral" """, false)]
    [InlineData("""<Properties><ResourcePackage>false</ResourcePackage></Properties>""", "", false)]
    public void A_resource_package_is_recognised_by_its_declaration_or_its_split_id(string properties, string identityExtra, bool expected)
    {
        var manifest = AppxManifest.Parse($"""
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Contoso.App" Publisher="CN=Contoso" Version="1.0.0.0" {identityExtra}/>
              {properties}
            </Package>
            """).ShouldNotBeNull();

        manifest.IsResourcePackage.ShouldBe(expected);
    }

    /// <summary>A resource reference is not a name. It is reported as absent, not as the reference.</summary>
    [Fact]
    public void A_display_name_given_by_resource_reference_is_absent()
    {
        var manifest = AppxManifest.Parse("""
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Microsoft.WindowsTerminal" Publisher="CN=Microsoft Corporation" Version="1.24.0.0" />
              <Properties>
                <DisplayName>ms-resource:AppName</DisplayName>
                <PublisherDisplayName>ms-resource:PublisherName</PublisherDisplayName>
              </Properties>
            </Package>
            """).ShouldNotBeNull();

        manifest.DisplayName.ShouldBeNull();
        manifest.PublisherDisplayName.ShouldBeNull();
        manifest.Name.ShouldBe("Microsoft.WindowsTerminal");
    }

    [Fact]
    public void Unknown_elements_anywhere_are_stepped_over()
    {
        var manifest = AppxManifest.Parse("""
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Unknown><Deep><Deeper /></Deep></Unknown>
              <Identity Name="Contoso.App" Publisher="CN=Contoso" Version="1.0.0.0" />
              <Properties>
                <Logo>a.png</Logo>
                <Odd><Nested>x</Nested></Odd>
                <DisplayName>Contoso</DisplayName>
              </Properties>
              <Applications>
                <Extension />
                <Application Id="One" Executable="one.exe"><uap:Something xmlns:uap="urn:x" /></Application>
                <Application Id="Two" Executable="two.exe" />
              </Applications>
              <Trailing />
            </Package>
            """).ShouldNotBeNull();

        manifest.DisplayName.ShouldBe("Contoso");
        manifest.Executable.ShouldBe("one.exe");
    }

    [Fact]
    public void A_manifest_without_an_identity_is_not_a_manifest()
    {
        AppxManifest.Parse("""
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Properties><DisplayName>Nameless</DisplayName></Properties>
            </Package>
            """).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not xml at all")]
    [InlineData("<Package>")]
    [InlineData("<Other><Identity Name=\"x\" /></Other>")]
    [InlineData("<Package><Identity Name=\"x\" /><Properties><DisplayName>unterminated</Properties></Package>")]
    public void Anything_that_is_not_a_well_formed_manifest_is_refused(string xml)
    {
        Should.NotThrow(() => AppxManifest.Parse(xml)).ShouldBeNull();
    }

    /// <summary>
    /// A manifest is a file in a package directory. It must not be able to make
    /// the reader fetch anything or expand anything.
    /// </summary>
    [Theory]
    [InlineData("""
        <!DOCTYPE Package [<!ENTITY xxe SYSTEM "file:///C:/Windows/win.ini">]>
        <Package><Identity Name="&xxe;" /></Package>
        """)]
    [InlineData("""
        <!DOCTYPE lol [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">]>
        <Package><Identity Name="&b;" /></Package>
        """)]
    public void External_entities_and_doctypes_are_refused(string xml)
    {
        Should.NotThrow(() => AppxManifest.Parse(xml)).ShouldBeNull();
    }

    [Fact]
    public void An_oversized_manifest_is_refused()
    {
        var padding = new string(' ', AppxManifest.MaxBytes + 1);

        AppxManifest.Parse("<Package><Identity Name=\"x\" />" + padding + "</Package>").ShouldBeNull();
    }

    [Fact]
    public void Reads_from_a_stream_the_same_way()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Slack));

        AppxManifest.Parse(stream).ShouldNotBeNull().Name.ShouldBe("com.tinyspeck.slackdesktop");
    }

    // ---- names --------------------------------------------------------------------------

    [Theory]
    [InlineData("com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g", "com.tinyspeck.slackdesktop_8yrtsj140pw4g")]
    [InlineData("Microsoft.LanguageExperiencePacken-GB_26100.9.16.0_neutral_split.language-en-gb_8wekyb3d8bbwe", "Microsoft.LanguageExperiencePacken-GB_8wekyb3d8bbwe")]
    [InlineData("windows.immersivecontrolpanel_10.0.8.1000_neutral_neutral_cw5n1h2txyewy", "windows.immersivecontrolpanel_cw5n1h2txyewy")]
    public void The_family_name_is_the_identity_name_and_the_publisher_id(string fullName, string family)
    {
        AppxManifest.FamilyNameOf(fullName).ShouldBe(family);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Slack")]
    [InlineData("a_b_c_d")]
    [InlineData("a_b_c_d_e_f")]
    [InlineData("_1.0_x64__pub")]
    [InlineData("name_1.0_x64__")]
    public void Anything_that_is_not_a_full_name_has_no_family(string? fullName)
    {
        AppxManifest.FamilyNameOf(fullName).ShouldBeNull();
        AppxManifest.FullNameParts(fullName).ShouldBeNull();
    }

    [Theory]
    [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", "Microsoft Corporation")]
    [InlineData("CN=\"Slack Technologies, LLC\", O=\"Slack Technologies, LLC\", C=US", "Slack Technologies, LLC")]
    [InlineData("O=Contoso, CN=Contoso Ltd", "Contoso Ltd")]
    [InlineData("CN=Solo", "Solo")]
    [InlineData("O=No common name", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_common_name_is_read_out_of_a_subject(string? subject, string? expected)
    {
        AppxManifest.CommonNameOf(subject).ShouldBe(expected);
    }
}
