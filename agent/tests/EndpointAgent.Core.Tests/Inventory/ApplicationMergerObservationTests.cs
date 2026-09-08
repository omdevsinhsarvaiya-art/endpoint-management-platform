using EndpointAgent.Core.Inventory;

namespace EndpointAgent.Core.Tests.Inventory;

/// <summary>
/// What a running process is allowed to claim: an installation's evidence when
/// it runs from inside one, an observed application when nothing covers it, a
/// transient when it is an installer or a run from a temp folder -- and, for
/// the observed, where Force Stop may and may not be pointed.
/// </summary>
public sealed class ApplicationMergerObservationTests
{
    private const string User = @"OMDEVSINH-TECHS\Techsara";

    private static SoftwareEvidence Registry(string name, string location, string? version = "1.0", string? publisher = "Contoso") =>
        new(EvidenceSource.UninstallRegistry, name, version, publisher, null, location, "x64");

    private static SoftwareEvidence Process(string path, string name, SoftwareScope scope = SoftwareScope.Machine, string? user = null) =>
        new(EvidenceSource.RunningProcess, name, Scope: scope, InstalledForUser: user, ExecutablePath: path);

    private static SoftwareEvidence Metadata(string path, string? product, string? company, string? version = "1.0.0.0") =>
        new(EvidenceSource.ExecutableMetadata, product, version, company, ExecutablePath: path, SignatureStatus: "Unsigned");

    // ---- attachment ----------------------------------------------------------------------

    [Fact]
    public void A_process_inside_an_installation_is_that_installations_evidence_and_changes_nothing()
    {
        var chrome = Registry("Google Chrome", @"C:\Program Files\Google\Chrome\Application", "152.0", "Google LLC");
        var process = Process(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "chrome");

        var alone = ApplicationMerger.Merge([chrome]).Single();
        var running = ApplicationMerger.Merge([chrome, process]).ShouldHaveSingleItem();

        running.Evidence.ShouldBe([chrome, process]);
        // The row -- the nine fields the report always carried -- is unchanged;
        // what discovery adds (the evidence, the executable) is not the row.
        SoftwareDiscoveryPipeline.Run([chrome, process]).Single()
            .ShouldSatisfyAllConditions(
                r => r.InstallLocation.ShouldBe(alone.InstallLocation),
                r => r.Name.ShouldBe(alone.Name),
                r => r.Version.ShouldBe(alone.Version),
                r => r.Publisher.ShouldBe(alone.Publisher),
                r => r.Evidence!.Count.ShouldBe(2));
        running.Confidence.ShouldBe(DiscoveryConfidence.Installed);
    }

    // ---- observed ------------------------------------------------------------------------

    /// <summary>The acceptance case: a portable executable running from Downloads.</summary>
    [Fact]
    public void A_portable_executable_running_from_downloads_is_observed_with_no_install_location()
    {
        var path = @"C:\Users\Techsara\Downloads\caffeine64.exe";

        var app = ApplicationMerger.Merge([
            Process(path, "caffeine64", SoftwareScope.User, User),
            Metadata(path, "Caffeine", "Zhorn Software", "1.97"),
        ]).ShouldHaveSingleItem();

        app.Confidence.ShouldBe(DiscoveryConfidence.Observed);
        app.Category.ShouldBe(ApplicationCategory.Observed);
        app.Name.ShouldBe("Caffeine");
        app.Version.ShouldBe("1.97");
        app.Publisher.ShouldBe("Zhorn Software");
        app.Scope.ShouldBe(SoftwareScope.User);
        app.InstalledForUser.ShouldBe(User);
        app.ExecutablePath.ShouldBe(path);
        app.InstallLocation.ShouldBeNull("Downloads is not an install location; Force Stop must not be pointed at it");
    }

    [Fact]
    public void An_observed_executable_in_its_own_directory_reports_that_directory()
    {
        var path = @"C:\Tools\Caffeine\caffeine64.exe";

        var app = ApplicationMerger.Merge([Process(path, "caffeine64")]).ShouldHaveSingleItem();

        app.Confidence.ShouldBe(DiscoveryConfidence.Observed);
        app.InstallLocation.ShouldBe(@"C:\Tools\Caffeine");
        ApplicationProcessMatcher.CanResolve(app.InstallLocation).ShouldBeTrue();
    }

    [Theory]
    [InlineData(@"C:\Users\Techsara\Desktop\tool.exe")]
    [InlineData(@"C:\Users\Techsara\Documents\tool.exe")]
    [InlineData(@"C:\Users\Techsara\tool.exe")]
    [InlineData(@"C:\Users\Public\tool.exe")]
    [InlineData(@"C:\tool.exe")]
    [InlineData(@"C:\Program Files\tool.exe")]
    public void A_shared_or_forbidden_directory_is_never_an_observed_applications_location(string path)
    {
        var app = ApplicationMerger.Merge([Process(path, "tool")]).ShouldHaveSingleItem();

        app.Confidence.ShouldBe(DiscoveryConfidence.Observed);
        app.InstallLocation.ShouldBeNull();
    }

    [Fact]
    public void An_observed_application_is_a_row()
    {
        var rows = SoftwareDiscoveryPipeline.Run([
            Process(@"C:\Users\Techsara\Downloads\caffeine64.exe", "caffeine64", SoftwareScope.User, User),
            Metadata(@"C:\Users\Techsara\Downloads\caffeine64.exe", "Caffeine", "Zhorn Software"),
        ]);

        var row = rows.ShouldHaveSingleItem();
        row.Name.ShouldBe("Caffeine");
        row.InstallationScope.ShouldBe("User");
        row.InstalledForUser.ShouldBe(User);
        row.InstallLocation.ShouldBeNull();
    }

    [Fact]
    public void Several_processes_of_one_executable_are_one_observation()
    {
        var path = @"C:\Tools\App\app.exe";

        var app = ApplicationMerger.Merge([Process(path, "app"), Process(path, "app"), Process(path, "app")]).ShouldHaveSingleItem();

        app.Evidence.Count.ShouldBe(3);
        SoftwareDiscoveryPipeline.Run([Process(path, "app"), Process(path, "app")]).Count.ShouldBe(1);
    }

    /// <summary>
    /// An observed executable whose file metadata names it exactly like an
    /// installed row is that row: the normalizer folds them, first record wins,
    /// and nothing is reported twice.
    /// </summary>
    [Fact]
    public void An_observed_executable_named_like_an_installed_row_does_not_duplicate_it()
    {
        var registry = new SoftwareEvidence(EvidenceSource.UninstallRegistry, "Microsoft OneDrive", "26.153.0809.0004", "Microsoft Corporation",
            null, @"C:\Users\Techsara\AppData\Local\Microsoft\OneDrive\26.153.0809.0004", null, SoftwareScope.User, User);
        var path = @"C:\Users\Techsara\AppData\Local\Microsoft\OneDrive\OneDrive.exe";

        var rows = SoftwareDiscoveryPipeline.Run([
            registry,
            Process(path, "OneDrive", SoftwareScope.User, User),
            Metadata(path, "Microsoft OneDrive", "Microsoft Corporation", "26.153.0809.0004"),
        ]);

        var row = rows.ShouldHaveSingleItem();
        row.InstallLocation.ShouldBe(@"C:\Users\Techsara\AppData\Local\Microsoft\OneDrive\26.153.0809.0004");
    }

    // ---- transient -----------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Users\Techsara\Downloads\ChromeSetup.exe")]
    [InlineData(@"C:\Users\Techsara\Downloads\node-v24-x64-installer.exe")]
    [InlineData(@"C:\Program Files (x86)\Google\Update\GoogleUpdate.exe")]
    [InlineData(@"C:\Users\Techsara\AppData\Local\Temp\7zS1234\app.exe")]
    [InlineData(@"C:\Windows\Temp\patch\tool.exe")]
    [InlineData(@"C:\Tools\vendor\unins000.exe")]
    [InlineData(@"C:\Tools\vendor\vc_redist.x64_bootstrapper.exe")]
    [InlineData(@"C:\Tools\vendor\Upgrade Assistant.exe")]
    public void An_installer_updater_or_temp_run_is_transient_and_not_a_row(string path)
    {
        ApplicationMerger.IsTransient(path).ShouldBeTrue();

        var app = ApplicationMerger.Merge([Process(path, "x")]).ShouldHaveSingleItem();

        app.Confidence.ShouldBe(DiscoveryConfidence.Transient);
        app.Category.ShouldBe(ApplicationCategory.Transient);
        app.InstallLocation.ShouldBeNull();
        SoftwareDiscoveryPipeline.Run([Process(path, "x")]).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(@"C:\Tools\Caffeine\caffeine64.exe")]
    [InlineData(@"C:\Program Files\Docker\Docker\com.docker.backend.exe")]
    [InlineData(@"C:\Users\Techsara\AppData\Local\Programs\antigravity\Antigravity.exe")]
    [InlineData(@"C:\Temporal\app.exe")]
    public void An_ordinary_executable_is_not_transient(string path)
    {
        ApplicationMerger.IsTransient(path).ShouldBeFalse();
    }

    /// <summary>
    /// The transient rule is about executables nothing covers. An application's
    /// own updater, inside its install directory, is the application's evidence.
    /// </summary>
    [Fact]
    public void An_updater_inside_an_installation_is_that_installations_evidence_not_a_transient()
    {
        var app = Registry("Contoso App", @"C:\Program Files\Contoso");
        var updater = Process(@"C:\Program Files\Contoso\ContosoUpdate.exe", "ContosoUpdate");

        var merged = ApplicationMerger.Merge([app, updater]).ShouldHaveSingleItem();

        merged.Confidence.ShouldBe(DiscoveryConfidence.Installed);
        merged.Evidence.ShouldBe([app, updater]);
    }

    [Fact]
    public void A_transient_is_still_kept_as_an_application_with_its_evidence()
    {
        var process = Process(@"C:\Users\Techsara\Downloads\ChromeSetup.exe", "ChromeSetup", SoftwareScope.User, User);

        var app = ApplicationMerger.Merge([process]).ShouldHaveSingleItem();

        app.Name.ShouldBe("ChromeSetup");
        app.Evidence.ShouldBe([process]);
        app.ExecutablePath.ShouldBe(@"C:\Users\Techsara\Downloads\ChromeSetup.exe");
    }

    // ---- reportability -------------------------------------------------------------------

    [Theory]
    [InlineData(DiscoveryConfidence.Installed, true)]
    [InlineData(DiscoveryConfidence.Observed, true)]
    [InlineData(DiscoveryConfidence.Referenced, false)]
    [InlineData(DiscoveryConfidence.Transient, false)]
    public void Only_installed_and_observed_applications_are_rows(DiscoveryConfidence confidence, bool reportable)
    {
        var app = new DiscoveredApplication(
            new ApplicationIdentity(IdentityKind.Executable, "x"), "X", null, null, null, null, null,
            SoftwareScope.Machine, null, null, confidence, ApplicationCategory.Application, []);

        SoftwareDiscoveryPipeline.IsReportable(app).ShouldBe(reportable);
    }
}
