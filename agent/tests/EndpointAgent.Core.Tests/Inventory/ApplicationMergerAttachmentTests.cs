using EndpointAgent.Core.Inventory;

namespace EndpointAgent.Core.Tests.Inventory;

/// <summary>
/// How supplementary evidence -- shortcuts, aliases, file metadata, processes --
/// joins the installations the registry reported, and what it becomes when it
/// joins nothing.
/// </summary>
/// <remarks>
/// The rules here decide two things an administrator sees: which evidence an
/// application carries, and -- for an installation that recorded no directory --
/// the directory Force Stop will act on. The second is why every adoption rule
/// is strict and every ambiguity resolves to "attach nothing".
/// </remarks>
public sealed class ApplicationMergerAttachmentTests
{
    private const string User = @"OMDEVSINH-TECHS\Techsara";

    private static SoftwareEvidence Registry(
        string name, string? version = "1.0", string? publisher = "Contoso", string? location = @"C:\Program Files\Contoso",
        SoftwareScope scope = SoftwareScope.Machine, string? user = null, string? view = "x64") =>
        new(EvidenceSource.UninstallRegistry, name, version, publisher, null, location, view, scope, user);

    private static SoftwareEvidence Shortcut(string name, string path, SoftwareScope scope = SoftwareScope.Machine, string? user = null) =>
        new(EvidenceSource.StartMenuShortcut, name, Scope: scope, InstalledForUser: user, ExecutablePath: path);

    private static SoftwareEvidence Alias(string path) =>
        new(EvidenceSource.AppPaths, ExecutablePath: path);

    private static SoftwareEvidence Metadata(string path, string? product = "Contoso App", string? company = "Contoso", string? version = "1.0.0.0", string? signer = "CN=Contoso Ltd") =>
        new(EvidenceSource.ExecutableMetadata, product, version, company, ExecutablePath: path, SignerSubject: signer, SignatureStatus: "Signed");

    private static SoftwareEvidence Process(string path, string? name = null) =>
        new(EvidenceSource.RunningProcess, name, ExecutablePath: path);

    /// <summary>The nine fields the report has always carried: the row, as distinct from what discovery adds to it.</summary>
    private static DiscoveredSoftware Row(DiscoveredApplication app)
    {
        var s = app.ToDiscoveredSoftware();
        return new DiscoveredSoftware(s.Name, s.Version, s.Publisher, s.InstallDate, s.InstallLocation, s.RegistryView, s.Scope, s.InstalledForUser, s.ProductCode);
    }

    // ---- stage 1: installations --------------------------------------------------------

    [Fact]
    public void Two_records_that_were_one_row_are_one_application_with_both_as_evidence()
    {
        var x86 = Registry("Google Chrome", "152.0", "Google LLC", @"C:\Program Files\Google\Chrome\Application", view: "x86");
        var x64 = Registry("Google Chrome", "152.0", "Google LLC", @"C:\Program Files\Google\Chrome\Application", view: "x64");

        var app = ApplicationMerger.Merge([x86, x64]).ShouldHaveSingleItem();

        app.Evidence.ShouldBe([x86, x64]);
        // The first record's fields are the row's, exactly as first-wins always made them.
        app.RegistryView.ShouldBe("x86");
    }

    [Fact]
    public void Records_the_inventory_kept_apart_stay_apart()
    {
        var apps = ApplicationMerger.Merge([
            Registry("Contoso Suite", "1.0"),
            Registry("Contoso Suite", "2.0"),
            Registry("Contoso Suite", "2.0", scope: SoftwareScope.User, user: User),
            Registry("Contoso Suite", "2.0", publisher: "Fabrikam"),
        ]);

        apps.Count.ShouldBe(4);
    }

    /// <summary>
    /// Two records with one row identity but different directories have different
    /// stable keys (a registered application's identity includes where it lives),
    /// so they are two applications -- and the normalizer still folds them into
    /// the one row it always did, first record winning, location-less as before.
    /// </summary>
    [Fact]
    public void A_second_record_never_changes_the_first_records_row()
    {
        var first = Registry("Contoso Suite", location: null);
        var second = Registry("Contoso Suite", location: @"C:\Program Files\Contoso");

        var apps = ApplicationMerger.Merge([first, second]);
        var row = SoftwareDiscoveryPipeline.Run([first, second]).ShouldHaveSingleItem();

        apps.Count.ShouldBe(2);
        apps.Select(a => a.Identity.StableKey).Distinct().Count().ShouldBe(2);
        row.InstallLocation.ShouldBeNull("the row was location-less before, and a second record was ignored before");
    }

    [Fact]
    public void The_upgrade_code_is_taken_from_whichever_record_has_it()
    {
        var product = "{A37AA9F3-1F80-41A2-A8F9-8B5417FAD4D7}";
        var apps = ApplicationMerger.Merge([
            new SoftwareEvidence(EvidenceSource.WindowsInstaller, "Agent", "1.8.0", "Techsara", ProductCode: product, UpgradeCode: "{8F3C1D92-6B74-4A5E-9D21-7C4E8B0F5A63}"),
        ]);

        apps.ShouldHaveSingleItem().UpgradeCode.ShouldBe("{8F3C1D92-6B74-4A5E-9D21-7C4E8B0F5A63}");
    }

    // ---- stage 2: attachment by containment -------------------------------------------

    [Fact]
    public void A_shortcut_inside_an_installations_directory_attaches_to_it()
    {
        var registry = Registry("Brave", "152.1", "Brave Software Inc", @"C:\Program Files\BraveSoftware\Brave-Browser\Application");
        var shortcut = Shortcut("Brave", @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe");

        var app = ApplicationMerger.Merge([registry, shortcut]).ShouldHaveSingleItem();

        app.Confidence.ShouldBe(DiscoveryConfidence.Installed);
        app.Evidence.ShouldBe([registry, shortcut]);
        app.ExecutablePath.ShouldBe(@"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe");
    }

    [Fact]
    public void Attachment_never_changes_the_row()
    {
        var registry = Registry("Brave", "152.1", "Brave Software Inc", @"C:\Program Files\BraveSoftware\Brave-Browser\Application");
        var shortcut = Shortcut("Brave", @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe");
        var metadata = Metadata(@"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe", "Brave Browser", "Brave Software, Inc.", "152.1.94.121");

        var alone = Row(ApplicationMerger.Merge([registry]).Single());
        var joined = Row(ApplicationMerger.Merge([registry, shortcut, metadata]).Single());

        joined.ShouldBe(alone);
    }

    [Fact]
    public void Containment_respects_the_directory_boundary()
    {
        var registry = Registry("Contoso", location: @"C:\Program Files\Contoso");
        var lookalike = Shortcut("Contoso Extra", @"C:\Program Files\ContosoExtra\app.exe");

        var apps = ApplicationMerger.Merge([registry, lookalike]);

        apps.Count.ShouldBe(2);
        apps[0].Evidence.ShouldHaveSingleItem();
        apps[1].Confidence.ShouldBe(DiscoveryConfidence.Referenced);
    }

    [Fact]
    public void A_quoted_install_location_still_contains_its_executable()
    {
        var registry = Registry("Visual Studio Installer", location: "\"C:\\Program Files (x86)\\Microsoft Visual Studio\\Installer\"");
        var shortcut = Shortcut("Visual Studio Installer", @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe");

        var app = ApplicationMerger.Merge([registry, shortcut]).ShouldHaveSingleItem();

        app.Evidence.Count.ShouldBe(2);
        app.InstallLocation.ShouldBe("\"C:\\Program Files (x86)\\Microsoft Visual Studio\\Installer\"", "the recorded value is reported as recorded");
    }

    /// <summary>
    /// Two installations recorded the same directory (an office suite does this).
    /// A shortcut named like one of them is that one's; a shortcut named like
    /// neither belongs to nobody rather than to both.
    /// </summary>
    [Fact]
    public void A_shared_directory_attaches_only_by_name()
    {
        var office = Registry("Microsoft 365 - en-us", "16.0", "Microsoft Corporation", @"C:\Program Files\Microsoft Office");
        var oneNote = Registry("Microsoft OneNote - en-us", "16.0", "Microsoft Corporation", @"C:\Program Files\Microsoft Office");
        var oneNoteShortcut = Shortcut("Microsoft OneNote - en-us", @"C:\Program Files\Microsoft Office\root\Office16\ONENOTE.EXE");
        var word = Shortcut("Word", @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE");

        var apps = ApplicationMerger.Merge([office, oneNote, oneNoteShortcut, word]);

        apps.Single(a => a.Name == "Microsoft OneNote - en-us").Evidence.ShouldBe([oneNote, oneNoteShortcut]);
        apps.Single(a => a.Name == "Microsoft 365 - en-us").Evidence.ShouldHaveSingleItem();
        apps.Single(a => a.Name == "Word").Confidence.ShouldBe(DiscoveryConfidence.Referenced);
    }

    // ---- stage 2: adoption ----------------------------------------------------------------

    [Fact]
    public void An_installation_without_a_directory_adopts_one_from_a_shortcut_named_like_it()
    {
        var registry = Registry("Python 3.14.7 (64-bit)", "3.14.7150.0", "Python Software Foundation", location: null, scope: SoftwareScope.User, user: User);
        var shortcut = Shortcut("Python 3.14.7 (64-bit)", @"C:\Users\Techsara\AppData\Local\Programs\Python\Python314\python.exe", SoftwareScope.User, User);

        var app = ApplicationMerger.Merge([registry, shortcut]).ShouldHaveSingleItem();

        app.InstallLocation.ShouldBe(@"C:\Users\Techsara\AppData\Local\Programs\Python\Python314");
        app.ExecutablePath.ShouldBe(@"C:\Users\Techsara\AppData\Local\Programs\Python\Python314\python.exe");
        app.Evidence.Count.ShouldBe(2);
    }

    [Fact]
    public void Adoption_leaves_every_other_row_field_alone()
    {
        var registry = Registry("Python 3.14.7 (64-bit)", "3.14.7150.0", "Python Software Foundation", location: null);
        var shortcut = Shortcut("Python 3.14.7 (64-bit)", @"C:\Python314\python.exe");

        var alone = Row(ApplicationMerger.Merge([registry]).Single());
        var adopted = Row(ApplicationMerger.Merge([registry, shortcut]).Single());

        adopted.ShouldBe(alone with { InstallLocation = @"C:\Python314" });
    }

    [Fact]
    public void Adoption_requires_an_exact_name()
    {
        var registry = Registry("Python 3.14.7 (64-bit)", location: null);
        var nearly = Shortcut("Python 3.14 (64-bit)", @"C:\Python314\python.exe");

        var apps = ApplicationMerger.Merge([registry, nearly]);

        apps.Single(a => a.Confidence == DiscoveryConfidence.Installed).InstallLocation.ShouldBeNull();
        apps.Count(a => a.Confidence == DiscoveryConfidence.Referenced).ShouldBe(1);
    }

    [Fact]
    public void Adoption_requires_the_publishers_to_agree_when_both_say()
    {
        var registry = Registry("Contoso App", publisher: "Contoso", location: null);
        var otherPublisher = Metadata(@"C:\Tools\Contoso App\app.exe", product: "Contoso App", company: "Fabrikam");
        var noPublisher = Metadata(@"C:\Tools\Contoso App\app.exe", product: "Contoso App", company: null);

        ApplicationMerger.Merge([registry, otherPublisher]).Single(a => a.Confidence == DiscoveryConfidence.Installed)
            .InstallLocation.ShouldBeNull();
        ApplicationMerger.Merge([registry, noPublisher]).Single(a => a.Confidence == DiscoveryConfidence.Installed)
            .InstallLocation.ShouldBe(@"C:\Tools\Contoso App");
    }

    [Fact]
    public void Adoption_requires_the_same_scope_and_account()
    {
        var machine = Registry("Contoso App", location: null);
        var perUser = Registry("Contoso App", location: null, scope: SoftwareScope.User, user: User);

        var userShortcut = Shortcut("Contoso App", @"C:\Users\Techsara\Tools\app.exe", SoftwareScope.User, User);
        var otherUserShortcut = Shortcut("Contoso App", @"C:\Users\Other\Tools\app.exe", SoftwareScope.User, @"OMDEVSINH-TECHS\Other");

        ApplicationMerger.Merge([machine, userShortcut]).Single(a => a.Confidence == DiscoveryConfidence.Installed)
            .InstallLocation.ShouldBeNull("a machine-wide record does not adopt a user's shortcut");
        ApplicationMerger.Merge([perUser, otherUserShortcut]).Single(a => a.Confidence == DiscoveryConfidence.Installed)
            .InstallLocation.ShouldBeNull("another account's shortcut is not this installation's");
        ApplicationMerger.Merge([perUser, userShortcut]).Single(a => a.Confidence == DiscoveryConfidence.Installed)
            .InstallLocation.ShouldBe(@"C:\Users\Techsara\Tools");
    }

    [Fact]
    public void Adoption_is_refused_when_two_installations_share_the_name()
    {
        var first = Registry("Contoso App", "1.0", location: null);
        var second = Registry("Contoso App", "2.0", location: null);
        var shortcut = Shortcut("Contoso App", @"C:\Tools\Contoso\app.exe");

        var apps = ApplicationMerger.Merge([first, second, shortcut]);

        apps.Where(a => a.Confidence == DiscoveryConfidence.Installed).ShouldAllBe(a => a.InstallLocation == null);
        apps.Single(a => a.Confidence == DiscoveryConfidence.Referenced).Evidence.ShouldBe([shortcut]);
    }

    /// <summary>
    /// The directory adopted becomes the root Force Stop terminates processes
    /// under. A root the matcher refuses is not adopted, however good the name
    /// match; the evidence still attaches.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData(@"C:\Program Files\app.exe")]
    [InlineData(@"C:\Users\app.exe")]
    [InlineData(@"C:\app.exe")]
    public void Adoption_refuses_a_directory_force_stop_could_not_act_on(string path)
    {
        var registry = Registry("Contoso App", location: null);
        var shortcut = Shortcut("Contoso App", path);

        var app = ApplicationMerger.Merge([registry, shortcut]).ShouldHaveSingleItem();

        app.InstallLocation.ShouldBeNull();
        app.Evidence.ShouldBe([registry, shortcut]);
    }

    [Fact]
    public void Two_different_directories_withdraw_the_adoption()
    {
        var registry = Registry("Contoso App", location: null);
        var first = Shortcut("Contoso App", @"C:\Tools\ContosoA\app.exe");
        var second = Shortcut("Contoso App", @"C:\Tools\ContosoB\app.exe");

        var app = ApplicationMerger.Merge([registry, first, second]).ShouldHaveSingleItem();

        app.InstallLocation.ShouldBeNull("two directories is a disagreement, not a choice");
        app.Evidence.Count.ShouldBe(3);
    }

    [Fact]
    public void Two_shortcuts_to_the_same_directory_agree()
    {
        var registry = Registry("Contoso App", location: null);
        var first = Shortcut("Contoso App", @"C:\Tools\Contoso\app.exe");
        var second = Shortcut("Contoso App", @"c:\tools\contoso\APP.EXE");

        ApplicationMerger.Merge([registry, first, second]).ShouldHaveSingleItem().InstallLocation.ShouldBe(@"C:\Tools\Contoso");
    }

    [Fact]
    public void An_adopted_directory_then_attracts_the_rest_of_its_evidence()
    {
        var registry = Registry("Contoso App", location: null);
        var shortcut = Shortcut("Contoso App", @"C:\Tools\Contoso\app.exe");
        var helper = Shortcut("Contoso Helper", @"C:\Tools\Contoso\helper.exe");

        var app = ApplicationMerger.Merge([registry, shortcut, helper]).ShouldHaveSingleItem();

        app.Evidence.ShouldBe([registry, shortcut, helper]);
    }

    // ---- stage 3: references ---------------------------------------------------------------

    [Fact]
    public void Everything_about_one_executable_is_one_reference()
    {
        var path = @"C:\Users\Techsara\Downloads\caffeine64.exe";
        var alias = Alias(path);
        var shortcut = Shortcut("Caffeine", @"c:\users\techsara\downloads\CAFFEINE64.EXE");
        var metadata = Metadata(path, "Caffeine", "Zhorn Software", "1.97.0.0", signer: null);

        var app = ApplicationMerger.Merge([alias, shortcut, metadata]).ShouldHaveSingleItem();

        app.Confidence.ShouldBe(DiscoveryConfidence.Referenced);
        app.Identity.Kind.ShouldBe(IdentityKind.Executable);
        app.Name.ShouldBe("Caffeine");
        app.Version.ShouldBe("1.97.0.0");
        app.Publisher.ShouldBe("Zhorn Software");
        app.ExecutablePath.ShouldBe(path);
        app.InstallLocation.ShouldBeNull("a reference is not an installation and has no install location");
        app.Evidence.ShouldBe([alias, shortcut, metadata]);
    }

    [Fact]
    public void A_reference_is_named_by_its_shortcut_then_its_product_name_then_its_file()
    {
        var path = @"C:\Tools\x\tool.exe";

        ApplicationMerger.Merge([Alias(path), Metadata(path, product: "Tool Product")]).Single().Name.ShouldBe("Tool Product");
        ApplicationMerger.Merge([Alias(path), Metadata(path, product: "Tool Product"), Shortcut("Tool", path)]).Single().Name.ShouldBe("Tool");
        ApplicationMerger.Merge([Alias(path)]).Single().Name.ShouldBe("tool");
    }

    [Fact]
    public void A_running_process_among_the_evidence_makes_a_reference_observed()
    {
        var path = @"C:\Users\Techsara\Downloads\caffeine64.exe";

        var app = ApplicationMerger.Merge([Process(path), Metadata(path, "Caffeine", "Zhorn Software", signer: null)]).ShouldHaveSingleItem();

        app.Confidence.ShouldBe(DiscoveryConfidence.Observed);
        app.Category.ShouldBe(ApplicationCategory.Observed);
        app.Name.ShouldBe("Caffeine");
        app.Publisher.ShouldBe("Zhorn Software");
    }

    [Fact]
    public void A_reference_carries_its_signer_as_a_claim()
    {
        var path = @"C:\Tools\x\tool.exe";

        var app = ApplicationMerger.Merge([Alias(path), Metadata(path, signer: "CN=Contoso Ltd, O=Contoso")]).ShouldHaveSingleItem();

        app.SignerSubject.ShouldBe("CN=Contoso Ltd, O=Contoso");
        app.SignatureStatus.ShouldBe("Signed");
    }

    [Fact]
    public void References_to_different_executables_are_different_applications()
    {
        var apps = ApplicationMerger.Merge([
            Shortcut("Git Bash", @"C:\Tools\Git\git-bash.exe"),
            Shortcut("Git GUI", @"C:\Tools\Git\cmd\git-gui.exe"),
        ]);

        apps.Count.ShouldBe(2);
    }

    [Fact]
    public void Evidence_naming_no_executable_stands_alone()
    {
        var apps = ApplicationMerger.Merge([Process("", "svc"), Process("", "svc")]);

        // Neither can be shown to be about the same thing; both are kept as they are.
        apps.Count.ShouldBe(2);
        apps.ShouldAllBe(a => a.Confidence == DiscoveryConfidence.Observed);
    }

    [Fact]
    public void Reference_scope_comes_from_what_pointed_at_it_not_from_the_file()
    {
        var path = @"C:\Users\Techsara\Tools\tool.exe";

        var app = ApplicationMerger.Merge([Shortcut("Tool", path, SoftwareScope.User, User), Metadata(path)]).ShouldHaveSingleItem();

        app.Scope.ShouldBe(SoftwareScope.User);
        app.InstalledForUser.ShouldBe(User);
    }

    // ---- primary executable and signer --------------------------------------------------

    [Fact]
    public void The_primary_executable_is_the_shortcut_named_like_the_application_then_the_alias()
    {
        var git = Registry("Git", "2.55", "The Git Development Community", @"C:\Program Files\Git");
        var bash = Shortcut("Git Bash", @"C:\Program Files\Git\git-bash.exe");
        var gui = Shortcut("Git GUI", @"C:\Program Files\Git\cmd\git-gui.exe");
        var alias = Alias(@"C:\Program Files\Git\cmd\git.exe");

        ApplicationMerger.Merge([git, bash, gui]).Single().ExecutablePath.ShouldBe(@"C:\Program Files\Git\git-bash.exe");
        ApplicationMerger.Merge([git, bash, gui, alias]).Single().ExecutablePath.ShouldBe(@"C:\Program Files\Git\cmd\git.exe");
        ApplicationMerger.Merge([git, bash, alias, Shortcut("Git", @"C:\Program Files\Git\git.exe")]).Single()
            .ExecutablePath.ShouldBe(@"C:\Program Files\Git\git.exe");
    }

    [Fact]
    public void The_signer_is_the_primary_executables()
    {
        var git = Registry("Git", "2.55", "The Git Development Community", @"C:\Program Files\Git");
        var primary = Shortcut("Git", @"C:\Program Files\Git\git.exe");
        var other = Shortcut("Git Bash", @"C:\Program Files\Git\git-bash.exe");

        var app = ApplicationMerger.Merge([
            git, other, primary,
            Metadata(@"C:\Program Files\Git\git-bash.exe", signer: "CN=Other"),
            Metadata(@"C:\Program Files\Git\git.exe", signer: "CN=Johannes Schindelin"),
        ]).ShouldHaveSingleItem();

        app.SignerSubject.ShouldBe("CN=Johannes Schindelin");
        app.SignatureStatus.ShouldBe("Signed");
    }

    /// <summary>
    /// A package registration declares its own executable and carries the
    /// publisher Windows verified. Neither needs a shortcut to be known.
    /// </summary>
    [Fact]
    public void An_installation_record_that_declares_its_executable_and_signer_keeps_them()
    {
        const string root = @"C:\Program Files\WindowsApps\Claude_1.30096.5.0_x64__pzs8sxrjxfjjc";
        var package = new SoftwareEvidence(EvidenceSource.PackageRegistration, "Claude", "1.30096.5.0", "Anthropic, PBC",
            InstallLocation: root, Scope: SoftwareScope.User, InstalledForUser: User,
            PackageFamilyName: "Claude_pzs8sxrjxfjjc", PackageFullName: "Claude_1.30096.5.0_x64__pzs8sxrjxfjjc",
            ExecutablePath: root + @"\Claude.exe", SignerSubject: "CN=Anthropic, PBC, O=Anthropic, PBC", SignatureStatus: "Signed");

        var app = ApplicationMerger.Merge([package]).ShouldHaveSingleItem();

        app.ExecutablePath.ShouldBe(root + @"\Claude.exe");
        app.SignerSubject.ShouldBe("CN=Anthropic, PBC, O=Anthropic, PBC");
        app.SignatureStatus.ShouldBe("Signed");
    }

    /// <summary>What Windows verified outranks what a file claims about itself.</summary>
    [Fact]
    public void The_installation_records_signer_outranks_file_metadata()
    {
        const string root = @"C:\Program Files\WindowsApps\Contoso.App_1.0.0.0_x64__abc";
        var package = new SoftwareEvidence(EvidenceSource.PackageRegistration, "Contoso App", "1.0.0.0", "Contoso",
            InstallLocation: root, PackageFamilyName: "Contoso.App_abc", ExecutablePath: root + @"\app.exe",
            SignerSubject: "CN=Contoso Ltd (verified)", SignatureStatus: "Signed");
        var claim = Metadata(root + @"\app.exe", signer: "CN=Contoso Ltd (claimed)");
        var shortcut = Shortcut("Contoso App", root + @"\launcher.exe");

        var app = ApplicationMerger.Merge([package, shortcut, claim]).ShouldHaveSingleItem();

        app.SignerSubject.ShouldBe("CN=Contoso Ltd (verified)");
        app.ExecutablePath.ShouldBe(root + @"\app.exe", "the declared executable outranks a same-named shortcut");
    }

    [Fact]
    public void An_installation_with_no_attached_executable_has_no_signer()
    {
        var app = ApplicationMerger.Merge([Registry("Contoso")]).ShouldHaveSingleItem();

        app.ExecutablePath.ShouldBeNull();
        app.SignerSubject.ShouldBeNull();
        app.SignatureStatus.ShouldBeNull();
    }

    // ---- category and packages ---------------------------------------------------------

    /// <summary>A source that knows what a thing is says so; the name heuristic is for the ones that cannot.</summary>
    [Fact]
    public void A_declared_category_wins_over_the_name_heuristic()
    {
        var framework = new SoftwareEvidence(EvidenceSource.PackageRegistration, "Contoso Application Suite", "1.0", "Contoso",
            InstallLocation: @"C:\Program Files\WindowsApps\Contoso.Suite_1.0.0.0_x64__abc", PackageFamilyName: "Contoso.Suite_abc",
            Category: ApplicationCategory.FrameworkOrResource);
        var inbox = new SoftwareEvidence(EvidenceSource.PackageRegistration, "Runtime Broker", "10.0", "Microsoft",
            InstallLocation: @"C:\Windows\SystemApps\Microsoft.Windows.X_cw5n1h2txyewy", PackageFamilyName: "Microsoft.Windows.X_cw5n1h2txyewy",
            Category: ApplicationCategory.InboxApp);
        var undeclared = new SoftwareEvidence(EvidenceSource.PackageRegistration, "Contoso Runtime", "1.0", "Contoso",
            InstallLocation: @"C:\Program Files\WindowsApps\Contoso.Runtime_1.0.0.0_x64__abc", PackageFamilyName: "Contoso.Runtime_abc");

        var apps = ApplicationMerger.Merge([framework, inbox, undeclared]);

        apps[0].Category.ShouldBe(ApplicationCategory.FrameworkOrResource);
        apps[1].Category.ShouldBe(ApplicationCategory.InboxApp);
        apps[2].Category.ShouldBe(ApplicationCategory.RuntimeOrSdk, "no declaration, so the name decides");
    }

    [Fact]
    public void A_declared_category_never_drops_the_row()
    {
        var framework = new SoftwareEvidence(EvidenceSource.PackageRegistration, "Microsoft.VCLibs.140.00", "14.0.33728.0", "Microsoft Corporation",
            InstallLocation: @"C:\Program Files\WindowsApps\Microsoft.VCLibs.140.00_14.0.33728.0_x64__8wekyb3d8bbwe",
            PackageFamilyName: "Microsoft.VCLibs.140.00_8wekyb3d8bbwe", Category: ApplicationCategory.FrameworkOrResource);

        SoftwareDiscoveryPipeline.Run([framework]).ShouldHaveSingleItem().Name.ShouldBe("Microsoft.VCLibs.140.00");
    }

    [Fact]
    public void A_packaged_application_attaches_the_shortcut_that_points_into_its_root()
    {
        const string root = @"C:\Program Files\WindowsApps\com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g";
        var slack = new SoftwareEvidence(EvidenceSource.PackageRegistration, "Slack", "4.52.155.0", "Slack Technologies Inc.",
            InstallLocation: root, Scope: SoftwareScope.User, InstalledForUser: User,
            PackageFamilyName: "com.tinyspeck.slackdesktop_8yrtsj140pw4g",
            PackageFullName: "com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g",
            ExecutablePath: root + @"\app\Slack.exe");
        var shortcut = Shortcut("Slack", root + @"\app\Slack.exe", SoftwareScope.User, User);

        var app = ApplicationMerger.Merge([slack, shortcut]).ShouldHaveSingleItem();

        app.Identity.Kind.ShouldBe(IdentityKind.Package);
        app.Identity.StableKey.ShouldBe("com.tinyspeck.slackdesktop_8yrtsj140pw4g");
        app.Identity.VersionKey.ShouldBe("com.tinyspeck.slackdesktop_4.52.155.0_x64__8yrtsj140pw4g");
        app.Confidence.ShouldBe(DiscoveryConfidence.Installed);
        app.Evidence.Count.ShouldBe(2);
        app.ExecutablePath.ShouldEndWith(@"\app\Slack.exe");
        app.ToDiscoveredSoftware().InstallLocation.ShouldBe(root);
    }

    // ---- ordering -------------------------------------------------------------------------

    [Fact]
    public void Installations_come_first_in_the_order_found_then_references()
    {
        var apps = ApplicationMerger.Merge([
            Shortcut("Loose", @"C:\Tools\loose.exe"),
            Registry("Beta", location: @"C:\Program Files\Beta"),
            Registry("Alpha", location: @"C:\Program Files\Alpha"),
        ]);

        apps.Select(a => a.Name).ShouldBe(["Beta", "Alpha", "Loose"]);
    }
}
