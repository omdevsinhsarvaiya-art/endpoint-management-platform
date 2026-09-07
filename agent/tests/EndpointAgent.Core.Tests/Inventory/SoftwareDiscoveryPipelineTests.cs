using EndpointAgent.Core.Inventory;

namespace EndpointAgent.Core.Tests.Inventory;

/// <summary>
/// Identity, evidence and merging -- the abstraction every discovery source will
/// report through.
/// </summary>
/// <remarks>
/// The subsystem exists because an application can be installed and running and
/// still be invisible: Slack is an MSIX package and writes no uninstall
/// registration, so the one source the platform reads has nothing to find. These
/// tests are about the decisions that let several sources describe one machine
/// without inventing applications or losing them.
/// </remarks>
public sealed class ApplicationIdentityTests
{
    private const char Sep = (char)0x1F;

    // ---- strongest identifier wins ------------------------------------------

    /// <summary>
    /// A package is identified by its family name, which Windows assigns and which
    /// survives every update; the full name, which carries the version, is what
    /// changed.
    /// </summary>
    [Fact]
    public void A_package_is_identified_by_its_family_name()
    {
        var identity = ApplicationIdentity.Derive(new SoftwareEvidence(
            EvidenceSource.PackageRegistration,
            Name: "Slack",
            Version: "4.52.155.0",
            PackageFamilyName: "com.tinyspeck.slackdesktop_8yrtsj140pw4g",
            PackageFullName: "com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g"));

        identity.ShouldNotBeNull();
        identity.Kind.ShouldBe(IdentityKind.Package);
        identity.StableKey.ShouldBe("com.tinyspeck.slackdesktop_8yrtsj140pw4g");
        identity.VersionKey.ShouldBe("com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g");
    }

    /// <summary>
    /// The upgrade code is the vendor's own statement that two releases are the
    /// same product, so it outranks the product code, which changes every release.
    /// </summary>
    [Fact]
    public void A_windows_installer_product_prefers_its_upgrade_code()
    {
        var identity = ApplicationIdentity.Derive(new SoftwareEvidence(
            EvidenceSource.WindowsInstaller,
            Name: "Endpoint Platform Agent",
            Version: "1.8.0",
            ProductCode: "{A37AA9F3-1F80-41A2-A8F9-8B5417FAD4D7}",
            UpgradeCode: "{8F3C1D92-6B74-4A5E-9D21-7C4E8B0F5A63}"));

        identity!.Kind.ShouldBe(IdentityKind.WindowsInstaller);
        identity.StableKey.ShouldBe("{8f3c1d92-6b74-4a5e-9d21-7c4e8b0f5a63}");
        identity.VersionKey.ShouldBe("{A37AA9F3-1F80-41A2-A8F9-8B5417FAD4D7}");
    }

    /// <summary>
    /// Two releases of one product share an upgrade code. They are the same
    /// application at different versions, which is what an update is.
    /// </summary>
    [Fact]
    public void Two_versions_of_one_installer_product_share_a_stable_key()
    {
        SoftwareEvidence Release(string product, string version) => new(
            EvidenceSource.WindowsInstaller, Name: "Endpoint Platform Agent", Version: version,
            ProductCode: product, UpgradeCode: "{8F3C1D92-6B74-4A5E-9D21-7C4E8B0F5A63}");

        var seventeen = ApplicationIdentity.Derive(Release("{C3470886-369C-40A5-8019-8A01D2D8DBBA}", "1.7.0"))!;
        var eighteen = ApplicationIdentity.Derive(Release("{A37AA9F3-1F80-41A2-A8F9-8B5417FAD4D7}", "1.8.0"))!;

        seventeen.StableKey.ShouldBe(eighteen.StableKey);
        seventeen.VersionKey.ShouldNotBe(eighteen.VersionKey);
    }

    [Fact]
    public void An_installer_product_without_an_upgrade_code_falls_back_to_its_product_code()
    {
        var identity = ApplicationIdentity.Derive(new SoftwareEvidence(
            EvidenceSource.WindowsInstaller, Name: "WireGuard", Version: "1.1",
            ProductCode: "{11111111-2222-3333-4444-555555555555}"));

        identity!.Kind.ShouldBe(IdentityKind.WindowsInstaller);
        identity.StableKey.ShouldBe("{11111111-2222-3333-4444-555555555555}");
    }

    // ---- composed identity, when there is no installer identifier -------------

    /// <summary>
    /// An EXE installer's registration offers no identifier of its own, so identity
    /// is composed from what can be observed: who vouches for it, what it calls
    /// itself, and where it lives.
    /// </summary>
    [Fact]
    public void A_registered_application_is_identified_by_publisher_name_and_directory()
    {
        var identity = ApplicationIdentity.Derive(new SoftwareEvidence(
            EvidenceSource.UninstallRegistry,
            Name: "Google Chrome", Version: "152.0", Publisher: "Google LLC",
            InstallLocation: @"C:\Program Files\Google\Chrome\Application"));

        identity!.Kind.ShouldBe(IdentityKind.Registered);
        identity.StableKey.ShouldBe(
            $"google llc{Sep}google chrome{Sep}c:\\program files\\google\\chrome\\application");
    }

    /// <summary>A signature is checkable; a registry string is not. The signer wins.</summary>
    [Fact]
    public void A_verified_signer_is_preferred_over_a_declared_publisher()
    {
        var identity = ApplicationIdentity.Derive(new SoftwareEvidence(
            EvidenceSource.UninstallRegistry,
            Name: "Contoso App", Publisher: "Contoso",
            InstallLocation: @"C:\Program Files\Contoso",
            SignerSubject: "CN=Contoso Ltd"));

        identity!.StableKey.ShouldStartWith("cn=contoso ltd");
    }

    /// <summary>
    /// The same directory written two ways is one application. Without this, a
    /// trailing separator or a capitalised drive letter would be a second row.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Program Files\Contoso")]
    [InlineData(@"C:\Program Files\Contoso\")]
    [InlineData(@"c:\program files\contoso")]
    [InlineData("\"C:\\Program Files\\Contoso\"")]
    public void Directory_spelling_does_not_create_a_second_identity(string location)
    {
        var identity = ApplicationIdentity.Derive(new SoftwareEvidence(
            EvidenceSource.UninstallRegistry, Name: "Contoso", Publisher: "Contoso", InstallLocation: location));

        identity!.StableKey.ShouldBe($"contoso{Sep}contoso{Sep}c:\\program files\\contoso");
    }

    /// <summary>
    /// A running executable with no installation record is identified by where it
    /// is, not by what it is called -- a portable binary in a downloads folder is
    /// not the installed application of the same name.
    /// </summary>
    [Fact]
    public void A_running_only_executable_is_identified_as_an_executable()
    {
        var identity = ApplicationIdentity.Derive(new SoftwareEvidence(
            EvidenceSource.RunningProcess,
            Name: "Caffeine Application",
            ExecutablePath: @"C:\Users\Techsara\Downloads\caffeine\caffeine64.exe"));

        identity!.Kind.ShouldBe(IdentityKind.Executable);
        identity.StableKey.ShouldEndWith(@"c:\users\techsara\downloads\caffeine");
    }

    /// <summary>
    /// The same product name in two places is two applications. This is the
    /// assumption the whole design refuses to make.
    /// </summary>
    [Fact]
    public void The_same_name_in_two_directories_is_two_identities()
    {
        var installed = ApplicationIdentity.Derive(new SoftwareEvidence(
            EvidenceSource.UninstallRegistry, Name: "Slack", Publisher: "Slack Technologies",
            InstallLocation: @"C:\Program Files\Slack"))!;
        var portable = ApplicationIdentity.Derive(new SoftwareEvidence(
            EvidenceSource.RunningProcess, Name: "Slack",
            ExecutablePath: @"C:\Users\Techsara\Downloads\slack.exe"))!;

        installed.StableKey.ShouldNotBe(portable.StableKey);
    }

    // ---- nothing to identify --------------------------------------------------

    [Fact]
    public void Evidence_that_identifies_nothing_yields_no_identity()
    {
        ApplicationIdentity.Derive(new SoftwareEvidence(EvidenceSource.AppPaths)).ShouldBeNull();
        ApplicationIdentity.Derive(new SoftwareEvidence(EvidenceSource.RunningProcess, Name: "   ")).ShouldBeNull();
    }

    [Fact]
    public void Deriving_from_null_is_refused_rather_than_guessed()
    {
        Should.Throw<ArgumentNullException>(() => ApplicationIdentity.Derive(null!));
    }
}

/// <summary>Turning evidence into applications: identity, confidence, category.</summary>
public sealed class ApplicationMergerTests
{
    // ---- confidence -----------------------------------------------------------

    /// <summary>An installation record, and only an installation record, means installed.</summary>
    [Theory]
    [InlineData(EvidenceSource.WindowsInstaller)]
    [InlineData(EvidenceSource.UninstallRegistry)]
    [InlineData(EvidenceSource.PackageRegistration)]
    public void Authoritative_evidence_makes_an_application_installed(EvidenceSource source)
    {
        var app = ApplicationMerger.Merge([
            new SoftwareEvidence(source, Name: "Contoso", Publisher: "Contoso", InstallLocation: @"C:\Program Files\Contoso"),
        ]).ShouldHaveSingleItem();

        app.Confidence.ShouldBe(DiscoveryConfidence.Installed);
    }

    /// <summary>
    /// Running is presence, not installation. Portable software looks exactly like
    /// this, and reporting it as installed would be a claim the machine does not
    /// support.
    /// </summary>
    [Fact]
    public void A_running_process_alone_is_observed_not_installed()
    {
        var app = ApplicationMerger.Merge([
            new SoftwareEvidence(EvidenceSource.RunningProcess, Name: "Caffeine",
                ExecutablePath: @"C:\Users\Techsara\Downloads\caffeine\caffeine64.exe"),
        ]).ShouldHaveSingleItem();

        app.Confidence.ShouldBe(DiscoveryConfidence.Observed);
        app.Category.ShouldBe(ApplicationCategory.Observed);
    }

    /// <summary>
    /// A shortcut pointing at something is not the something. A stale shortcut left
    /// behind by an uninstall looks precisely like this.
    /// </summary>
    [Theory]
    [InlineData(EvidenceSource.AppPaths)]
    [InlineData(EvidenceSource.StartMenuShortcut)]
    [InlineData(EvidenceSource.ExecutableMetadata)]
    public void A_pointer_to_an_application_is_only_a_reference(EvidenceSource source)
    {
        var app = ApplicationMerger.Merge([
            new SoftwareEvidence(source, Name: "Contoso", ExecutablePath: @"C:\Program Files\Contoso\app.exe"),
        ]).ShouldHaveSingleItem();

        app.Confidence.ShouldBe(DiscoveryConfidence.Referenced);
    }

    // ---- category -------------------------------------------------------------

    /// <summary>
    /// Categories label; they never drop. "What runtimes are on this fleet" is a
    /// real question, so a redistributable is collected and marked, not hidden at
    /// the source.
    /// </summary>
    [Theory]
    [InlineData("Microsoft Visual C++ v14 Redistributable (x64)", ApplicationCategory.RuntimeOrSdk)]
    [InlineData("Microsoft .NET SDK 10.0.400 (x64)", ApplicationCategory.RuntimeOrSdk)]
    [InlineData("Windows Software Development Kit - Windows 10", ApplicationCategory.RuntimeOrSdk)]
    [InlineData("Python Launcher", ApplicationCategory.RuntimeOrSdk)]
    [InlineData("Microsoft Windows Application Compatibility Fix Database", ApplicationCategory.Component)]
    [InlineData("Google Chrome", ApplicationCategory.Application)]
    [InlineData("Slack", ApplicationCategory.Application)]
    public void Software_is_categorised_by_what_it_is(string name, ApplicationCategory expected)
    {
        var app = ApplicationMerger.Merge([
            new SoftwareEvidence(EvidenceSource.UninstallRegistry, Name: name, InstallLocation: @"C:\Program Files\X"),
        ]).ShouldHaveSingleItem();

        app.Category.ShouldBe(expected);
    }

    // ---- what becomes an application, and what does not ----------------------

    [Fact]
    public void Evidence_with_no_identity_never_becomes_an_application()
    {
        ApplicationMerger.Merge([new SoftwareEvidence(EvidenceSource.AppPaths)]).ShouldBeEmpty();
    }

    /// <summary>
    /// A source that found only an executable still names something. Dropping it
    /// would lose exactly the running-but-unregistered application the subsystem
    /// exists to surface.
    /// </summary>
    [Fact]
    public void An_executable_with_no_declared_name_is_named_for_its_file()
    {
        var app = ApplicationMerger.Merge([
            new SoftwareEvidence(EvidenceSource.RunningProcess,
                ExecutablePath: @"C:\Users\Techsara\Downloads\caffeine\caffeine64.exe"),
        ]).ShouldHaveSingleItem();

        app.Name.ShouldBe("caffeine64");
    }

    [Fact]
    public void The_evidence_that_produced_an_application_is_kept_with_it()
    {
        var evidence = new SoftwareEvidence(
            EvidenceSource.PackageRegistration, Name: "Slack",
            PackageFamilyName: "com.tinyspeck.slackdesktop_8yrtsj140pw4g");

        var app = ApplicationMerger.Merge([evidence]).ShouldHaveSingleItem();

        app.Evidence.ShouldHaveSingleItem().ShouldBe(evidence);
    }

    /// <summary>Every descriptive field survives the projection unchanged.</summary>
    [Fact]
    public void An_applications_details_are_carried_through_untouched()
    {
        var app = ApplicationMerger.Merge([
            new SoftwareEvidence(
                EvidenceSource.UninstallRegistry,
                Name: "Zoom Workplace", Version: "7.1.5", Publisher: "Zoom",
                InstallDate: "20260904", InstallLocation: @"C:\Users\Techsara\AppData\Roaming\Zoom\bin",
                RegistryView: "x86", Scope: SoftwareScope.User, InstalledForUser: @"OMDEVSINH-TECHS\Techsara",
                ProductCode: "{9999}"),
        ]).ShouldHaveSingleItem();

        var projected = app.ToDiscoveredSoftware();
        projected.Name.ShouldBe("Zoom Workplace");
        projected.Version.ShouldBe("7.1.5");
        projected.Publisher.ShouldBe("Zoom");
        projected.InstallDate.ShouldBe("20260904");
        projected.InstallLocation.ShouldBe(@"C:\Users\Techsara\AppData\Roaming\Zoom\bin");
        projected.RegistryView.ShouldBe("x86");
        projected.Scope.ShouldBe(SoftwareScope.User);
        projected.InstalledForUser.ShouldBe(@"OMDEVSINH-TECHS\Techsara");
        projected.ProductCode.ShouldBe("{9999}");
    }

    [Fact]
    public void A_null_entry_in_the_evidence_does_not_throw()
    {
        ApplicationMerger.Merge([
            null!,
            new SoftwareEvidence(EvidenceSource.UninstallRegistry, Name: "Contoso", InstallLocation: @"C:\X\Y"),
        ]).Count.ShouldBe(1);
    }

    [Fact]
    public void Merging_null_is_refused_rather_than_guessed()
    {
        Should.Throw<ArgumentNullException>(() => ApplicationMerger.Merge(null!));
    }
}

/// <summary>
/// The regression gate for the abstraction: with only the uninstall registry
/// reporting, the pipeline reports exactly what it reported before.
/// </summary>
/// <remarks>
/// <para>
/// The registry collector now emits <see cref="SoftwareEvidence"/> and its result
/// travels evidence → merger → projection → normalizer instead of going straight
/// to the normalizer. That is a refactor of the path, and a refactor of the path
/// that changed the answer would be a silent inventory regression across the whole
/// fleet.
/// </para>
/// <para>
/// So the property is asserted directly: for the same registry findings, the new
/// pipeline and the old one produce equal lists. The fixture is shaped like the
/// real machine this was measured on -- machine-wide and per-user entries, the
/// same product under two registry views, several users with the same
/// application, and entries missing a version, a publisher or a location.
/// </para>
/// </remarks>
public sealed class SoftwareDiscoveryPipelineTests
{
    /// <summary>The registry findings, as the collector now reports them.</summary>
    private static SoftwareEvidence[] RegistryEvidence() =>
    [
        new(EvidenceSource.UninstallRegistry, "Google Chrome", "152.0.7977.77", "Google LLC", "20260901",
            @"C:\Program Files\Google\Chrome\Application", "x86"),
        // The same entry through the other registry view: Windows really does this.
        new(EvidenceSource.UninstallRegistry, "Google Chrome", "152.0.7977.77", "Google LLC", "20260901",
            @"C:\Program Files\Google\Chrome\Application", "x64"),
        new(EvidenceSource.WindowsInstaller, "WireGuard", "1.1", "WireGuard LLC", null,
            @"C:\Program Files\WireGuard", "x64", ProductCode: "{11111111-2222-3333-4444-555555555555}"),
        new(EvidenceSource.UninstallRegistry, "Brave", "152.1.94.121", "Brave Software Inc", null,
            @"C:\Program Files\BraveSoftware\Brave-Browser\Application", "x86"),
        // Per-user, several accounts, one product: four installations, not a duplicate.
        new(EvidenceSource.UninstallRegistry, "Microsoft OneDrive", "26.153.0809.0004", "Microsoft Corporation", null,
            @"C:\Users\Techsara\AppData\Local\Microsoft\OneDrive", null, SoftwareScope.User, @"OMDEVSINH-TECHS\Techsara"),
        new(EvidenceSource.UninstallRegistry, "Microsoft OneDrive", "26.153.0809.0004", "Microsoft Corporation", null,
            @"C:\Users\Administrator\AppData\Local\Microsoft\OneDrive", null, SoftwareScope.User, @"OMDEVSINH-TECHS\Administrator"),
        // Missing metadata must not drop the application.
        new(EvidenceSource.UninstallRegistry, "Microsoft Windows Application Compatibility Fix Database"),
        new(EvidenceSource.UninstallRegistry, "ASUS ExpertPanel", "1.0.47.0", null, null,
            @"C:\Program Files\ASUS ExpertPanel", "x64", ProductCode: "{22222222-3333-4444-5555-666666666666}"),
        new(EvidenceSource.UninstallRegistry, "Node.js", "24.19.0", "Node.js Foundation", null, null, "x64"),
        // Two versions of one product, side by side in their own directories.
        new(EvidenceSource.UninstallRegistry, "Python", "3.13.0", "Python Software Foundation", null,
            @"C:\Python313", "x64"),
        new(EvidenceSource.UninstallRegistry, "Python", "3.14.7", "Python Software Foundation", null,
            @"C:\Python314", "x64"),
        // Two versions sharing one directory, which is the case the two identity
        // rules disagree about: the normalizer keys on version and keeps both,
        // while the stable key deliberately excludes the version and would fold
        // them into one. The pipeline must still report what it always reported,
        // so this pair is what makes the equality below load-bearing rather than
        // incidental.
        new(EvidenceSource.UninstallRegistry, "Contoso Suite", "1.0", "Contoso", null,
            @"C:\Program Files\Contoso Suite", "x64"),
        new(EvidenceSource.UninstallRegistry, "Contoso Suite", "2.0", "Contoso", null,
            @"C:\Program Files\Contoso Suite", "x64"),
    ];

    /// <summary>The same findings as the collector reported them before the refactor.</summary>
    private static IEnumerable<DiscoveredSoftware> AsDiscoveredSoftware(IEnumerable<SoftwareEvidence> evidence) =>
        evidence.Select(e => new DiscoveredSoftware(
            e.Name, e.Version, e.Publisher, e.InstallDate, e.InstallLocation,
            e.RegistryView, e.Scope, e.InstalledForUser, e.ProductCode));

    [Fact]
    public void The_pipeline_reports_what_the_collector_reported_before_it_existed()
    {
        var evidence = RegistryEvidence();

        var before = SoftwareInventoryNormalizer.Normalize(AsDiscoveredSoftware(evidence));
        var after = SoftwareInventoryNormalizer.Normalize(
            ApplicationMerger.Merge(evidence).Select(a => a.ToDiscoveredSoftware()));

        after.ShouldBe(before);
    }

    /// <summary>
    /// The properties the equality above rests on, stated so a failure says which
    /// one broke rather than only that a list differed.
    /// </summary>
    [Fact]
    public void The_pipeline_preserves_the_existing_inventory_rules()
    {
        var reported = SoftwareInventoryNormalizer.Normalize(
            ApplicationMerger.Merge(RegistryEvidence()).Select(a => a.ToDiscoveredSoftware()));

        // Two registry views of one entry collapse to one application.
        reported.Count(s => s.Name == "Google Chrome").ShouldBe(1);

        // The same product for two people stays two installations.
        reported.Count(s => s.Name == "Microsoft OneDrive").ShouldBe(2);
        reported.Where(s => s.Name == "Microsoft OneDrive")
            .Select(s => s.InstalledForUser).ShouldBe(
                [@"OMDEVSINH-TECHS\Administrator", @"OMDEVSINH-TECHS\Techsara"], ignoreOrder: true);

        // Side-by-side versions both stay visible, including when they share a
        // directory and therefore a stable key.
        reported.Count(s => s.Name == "Python").ShouldBe(2);
        reported.Count(s => s.Name == "Contoso Suite").ShouldBe(2);

        // Absent metadata is absent, not invented, and does not drop the row.
        var compat = reported.Single(
            s => s.Name == "Microsoft Windows Application Compatibility Fix Database");
        compat.Version.ShouldBeNull();
        compat.Publisher.ShouldBeNull();

        // A machine-wide entry never carries a user.
        reported.Where(s => s.InstallationScope == "Machine").ShouldAllBe(s => s.InstalledForUser == null);
    }

    /// <summary>
    /// Identity is computed for every registry finding, so the later phases have
    /// something to fold on -- but it does not yet decide what is reported.
    /// </summary>
    [Fact]
    public void Every_registry_finding_gets_an_identity_without_changing_the_report()
    {
        var applications = ApplicationMerger.Merge(RegistryEvidence());

        applications.ShouldAllBe(a => a.Identity.StableKey.Length > 0);
        applications.ShouldAllBe(a => a.Confidence == DiscoveryConfidence.Installed);
        applications.Count(a => a.Identity.Kind == IdentityKind.WindowsInstaller).ShouldBe(2);
        applications.Count(a => a.Identity.Kind == IdentityKind.Registered).ShouldBe(11);

        // The two Contoso Suite versions share a stable key and are still two
        // applications at this stage: folding is not this phase's job.
        applications.Where(a => a.Name == "Contoso Suite")
            .Select(a => a.Identity.StableKey).Distinct().Count().ShouldBe(1);
    }
}
