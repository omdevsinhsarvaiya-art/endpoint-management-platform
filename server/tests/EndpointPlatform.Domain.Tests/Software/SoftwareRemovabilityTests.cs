using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Software;

namespace EndpointPlatform.Domain.Tests.Software;

/// <summary>
/// The removability table, row by row. Every decision here is a refusal or a
/// method; proving it with fixtures is what lets the platform say "not
/// removable, and here is why" instead of queueing a task to find out.
/// </summary>
public sealed class SoftwareRemovabilityTests
{
    private static readonly Guid DeviceId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private const string ChromeCode = "{8A69D345-D564-463C-AFF1-A69D9E530F96}";
    private const string SlackFullName = "com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g";
    private const string SlackFamily = "com.tinyspeck.slackdesktop_8yrtsj140pw4g";

    private static DeviceSoftware Row(
        string name = "Google Chrome",
        string? scope = "Machine",
        string? productCode = ChromeCode,
        string? identityKind = "WindowsInstaller",
        string? confidence = "Installed",
        string? category = "Application",
        string? packageFamilyName = null,
        string? packageFullName = null,
        string? upgradeCode = null) =>
        new(DeviceId, name, "152.0.1", "Google LLC", null, @"C:\Program Files\Google\Chrome\Application", "x86", Now,
            scope, scope == "User" ? @"CORP\alice" : null, productCode, identityKind, null, null, confidence, category,
            packageFamilyName, packageFullName, upgradeCode);

    private static DeviceSoftware Package(
        string name = "Slack",
        string? category = "Application",
        string? family = SlackFamily,
        string? fullName = SlackFullName) =>
        Row(name, scope: "User", productCode: null, identityKind: "Package", category: category,
            packageFamilyName: family, packageFullName: fullName);

    // ------------------------------------------------------- Windows Installer

    [Fact]
    public void A_machine_wide_windows_installer_product_is_removed_by_product_code()
    {
        var decision = SoftwareRemovability.Evaluate(Row());

        decision.Removable.ShouldBeTrue();
        decision.Method.ShouldBe(ApplicationRemovalMethod.WindowsInstaller);
        decision.Reason.ShouldBeNull();
    }

    /// <summary>
    /// The agent runs as SYSTEM, and msi.dll in that context cannot see a product
    /// registered in another account's profile. A row with no scope at all is
    /// not known to be machine-wide, and only machine-wide is acted on.
    /// </summary>
    [Theory]
    [InlineData("User")]
    [InlineData(null)]
    public void A_windows_installer_product_that_is_not_machine_wide_is_refused(string? scope)
    {
        var decision = SoftwareRemovability.Evaluate(Row(scope: scope));

        decision.Removable.ShouldBeFalse();
        decision.Method.ShouldBeNull();
        decision.Reason.ShouldBe(NotRemovableReason.PerUserInstall);
    }

    [Fact]
    public void A_windows_installer_row_without_a_product_code_has_nothing_to_hand_to_windows()
    {
        SoftwareRemovability.Evaluate(Row(productCode: null))
            .Reason.ShouldBe(NotRemovableReason.NoInstallerIdentity);
    }

    // ----------------------------------------------------------------- packages

    [Fact]
    public void A_package_is_removed_by_full_name()
    {
        var decision = SoftwareRemovability.Evaluate(Package());

        decision.Removable.ShouldBeTrue();
        decision.Method.ShouldBe(ApplicationRemovalMethod.Package);
        decision.Reason.ShouldBeNull();
    }

    [Fact]
    public void A_package_without_a_full_name_has_nothing_to_hand_to_windows()
    {
        SoftwareRemovability.Evaluate(Package(fullName: null))
            .Reason.ShouldBe(NotRemovableReason.NoInstallerIdentity);
    }

    /// <summary>Every Windows inbox package carries the same publisher id.</summary>
    [Theory]
    [InlineData("windows.immersivecontrolpanel_cw5n1h2txyewy", "windows.immersivecontrolpanel_10.0.6.1000_neutral_neutral_cw5n1h2txyewy")]
    [InlineData(null, "Microsoft.Windows.ShellExperienceHost_10.0.26100.1_neutral_neutral_cw5n1h2txyewy")]
    [InlineData("Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy", "Microsoft.Windows.StartMenuExperienceHost_10.0.26100.1_neutral_neutral_CW5N1H2TXYEWY")]
    public void A_windows_inbox_package_is_a_system_component(string? family, string fullName)
    {
        var decision = SoftwareRemovability.Evaluate(Package("Settings", family: family, fullName: fullName));

        decision.Removable.ShouldBeFalse();
        decision.Reason.ShouldBe(NotRemovableReason.SystemComponent);
    }

    [Theory]
    [InlineData("InboxApp")]
    [InlineData("FrameworkOrResource")]
    [InlineData("Component")]
    public void A_package_in_a_system_category_is_a_system_component(string category)
    {
        var decision = SoftwareRemovability.Evaluate(Package("Microsoft.VCLibs.140.00", category: category,
            family: "Microsoft.VCLibs.140.00_8wekyb3d8bbwe", fullName: "Microsoft.VCLibs.140.00_14.0.33728.0_x64__8wekyb3d8bbwe"));

        decision.Removable.ShouldBeFalse();
        decision.Reason.ShouldBe(NotRemovableReason.SystemComponent);
    }

    /// <summary>
    /// A publisher other than Windows' in an ordinary category is a third-party
    /// package, whatever the store it came from.
    /// </summary>
    [Fact]
    public void A_third_party_package_is_not_mistaken_for_a_system_component()
    {
        SoftwareRemovability.Evaluate(Package(category: "RuntimeOrSdk")).Removable.ShouldBeTrue();
    }

    // -------------------------------------------------------- self-protection

    [Fact]
    public void The_agent_is_refused_by_name()
    {
        var decision = SoftwareRemovability.Evaluate(Row(name: "Endpoint Platform Agent"));

        decision.Removable.ShouldBeFalse();
        decision.Reason.ShouldBe(NotRemovableReason.ProtectedAgent);
    }

    /// <summary>
    /// The upgrade code survives every release and any renamed display name, and
    /// case is not identity for a GUID.
    /// </summary>
    [Theory]
    [InlineData("{8F3C1D92-6B74-4A5E-9D21-7C4E8B0F5A63}")]
    [InlineData("{8f3c1d92-6b74-4a5e-9d21-7c4e8b0f5a63}")]
    public void The_agent_is_refused_by_upgrade_code_whatever_it_is_called(string upgradeCode)
    {
        var decision = SoftwareRemovability.Evaluate(Row(name: "Something Renamed", upgradeCode: upgradeCode));

        decision.Removable.ShouldBeFalse();
        decision.Reason.ShouldBe(NotRemovableReason.ProtectedAgent);
    }

    /// <summary>Self-protection wins over every identity the row could carry.</summary>
    [Fact]
    public void The_agent_is_refused_even_as_a_machine_wide_windows_installer_product()
    {
        var decision = SoftwareRemovability.Evaluate(
            Row(name: "endpoint platform agent", scope: "Machine", productCode: ChromeCode, identityKind: "WindowsInstaller"));

        decision.Removable.ShouldBeFalse();
        decision.Method.ShouldBeNull();
        decision.Reason.ShouldBe(NotRemovableReason.ProtectedAgent);
    }

    // ------------------------------------------------- no installer identity

    /// <summary>
    /// An EXE-installer registration, an observed executable, and a row from an
    /// agent older than 1.9.0: none has an identity Windows removes through a
    /// typed call, and their uninstallers are programs the agent does not launch.
    /// </summary>
    [Theory]
    [InlineData("Registered", "Installed")]
    [InlineData("Executable", "Observed")]
    [InlineData(null, null)]
    public void A_row_without_a_typed_installer_identity_is_refused(string? identityKind, string? confidence)
    {
        var decision = SoftwareRemovability.Evaluate(
            Row(productCode: null, identityKind: identityKind, confidence: confidence));

        decision.Removable.ShouldBeFalse();
        decision.Method.ShouldBeNull();
        decision.Reason.ShouldBe(NotRemovableReason.NoInstallerIdentity);
    }

    /// <summary>
    /// A product code alone does not make a Windows Installer product: an agent
    /// older than 1.9.0 reported no identity, and its rows are not acted on.
    /// </summary>
    [Fact]
    public void A_product_code_from_an_agent_that_reported_no_identity_is_not_enough()
    {
        SoftwareRemovability.Evaluate(Row(identityKind: null, confidence: null, category: null))
            .Reason.ShouldBe(NotRemovableReason.NoInstallerIdentity);
    }

    // -------------------------------------------------------------------- wire

    /// <summary>
    /// The console and the agent match on these exact names; they are the
    /// contract, so they are pinned.
    /// </summary>
    [Fact]
    public void Methods_and_reasons_travel_by_the_names_the_console_matches_on()
    {
        ApplicationRemovalMethod.WindowsInstaller.ToString().ShouldBe("WindowsInstaller");
        ApplicationRemovalMethod.Package.ToString().ShouldBe("Package");

        Enum.GetNames<NotRemovableReason>()
            .ShouldBe(["PerUserInstall", "SystemComponent", "ProtectedAgent", "NoInstallerIdentity"]);
    }
}
