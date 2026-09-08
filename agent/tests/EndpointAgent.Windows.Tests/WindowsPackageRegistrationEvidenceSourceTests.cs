using System.Security.Principal;
using EndpointAgent.Core.Inventory;
using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// MSIX/AppX package registrations, read from this machine.
/// </summary>
/// <remarks>
/// Every Windows 10/11 machine registers packages for each signed-in account, so
/// the shape is asserted unconditionally; the one named application -- Slack,
/// the case that started this milestone -- is asserted only where it is present.
/// </remarks>
public sealed class WindowsPackageRegistrationEvidenceSourceTests
{
    private static WindowsPackageRegistrationEvidenceSource Create() =>
        new(NullLogger<WindowsPackageRegistrationEvidenceSource>.Instance);

    private static string CurrentAccount() => WindowsIdentity.GetCurrent().Name;

    [Fact]
    public async Task Reports_packages_registered_for_the_signed_in_user()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        evidence.ShouldContain(e => e.Scope == SoftwareScope.User
            && string.Equals(e.InstalledForUser, CurrentAccount(), StringComparison.OrdinalIgnoreCase));
        evidence.ShouldAllBe(e => e.Source == EvidenceSource.PackageRegistration);
        evidence.ShouldAllBe(e => e.IsAuthoritative);
    }

    [Fact]
    public async Task Every_package_has_a_family_identity_a_version_and_a_root_that_exists()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        evidence.ShouldAllBe(e => !string.IsNullOrWhiteSpace(e.Name));
        evidence.ShouldAllBe(e => !string.IsNullOrWhiteSpace(e.Version));
        evidence.ShouldAllBe(e => e.PackageFullName != null && e.PackageFamilyName == AppxManifest.FamilyNameOf(e.PackageFullName));
        evidence.ShouldAllBe(e => e.InstallLocation != null && ExecutablePath.Normalize(e.InstallLocation) == e.InstallLocation);
        evidence.ShouldAllBe(e => Directory.Exists(e.InstallLocation!));
        evidence.ShouldAllBe(e => e.ProductCode == null && e.UpgradeCode == null);
    }

    /// <summary>A resource reference is not a name; every name here is text a person could read.</summary>
    [Fact]
    public async Task Names_are_text_not_resource_references()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        evidence.ShouldAllBe(e => !e.Name!.StartsWith("@", StringComparison.Ordinal));
        evidence.ShouldAllBe(e => !e.Name!.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The superseded-registration rule, cross-checked on the real machine: where
    /// a reported package's root holds a manifest, that manifest is for the
    /// reported version. A registration replaced by a later version would fail
    /// this, because the shared root now holds the later version's manifest.
    /// </summary>
    [Fact]
    public async Task Every_reported_package_is_the_version_its_root_holds()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        var mismatched = new List<string>();
        foreach (var package in evidence)
        {
            var path = Path.Combine(package.InstallLocation!, "AppxManifest.xml");
            if (!File.Exists(path))
            {
                continue;
            }

            using var stream = File.OpenRead(path);
            var manifest = AppxManifest.Parse(stream);
            if (manifest?.Version is not null && !string.Equals(manifest.Version, package.Version, StringComparison.OrdinalIgnoreCase))
            {
                mismatched.Add($"{package.PackageFullName}: root holds {manifest.Version}");
            }
        }

        mismatched.ShouldBeEmpty();
    }

    [Fact]
    public async Task No_family_is_reported_twice_for_one_account_from_a_shared_root()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        // A per-family root (the Windows SystemApps layout) can hold one version;
        // two reports from one such root would be one deployed and one superseded.
        var duplicates = evidence
            .GroupBy(e => $"{e.InstallLocation}|{e.Scope}|{e.InstalledForUser}", StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        duplicates.ShouldBeEmpty();
    }

    [Fact]
    public async Task Frameworks_resources_and_inbox_packages_are_categorised_not_dropped()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // Every Windows machine has both kinds.
        evidence.ShouldContain(e => e.Category == ApplicationCategory.FrameworkOrResource);
        evidence.ShouldContain(e => e.Category == ApplicationCategory.InboxApp);

        evidence.Where(e => ExecutablePath.IsUnder(e.InstallLocation, windows) && e.Category != ApplicationCategory.FrameworkOrResource)
            .ShouldAllBe(e => e.Category == ApplicationCategory.InboxApp);
    }

    [Fact]
    public async Task A_declared_executable_is_inside_its_package_and_exists()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        var withExecutable = evidence.Where(e => e.ExecutablePath is not null).ToList();
        withExecutable.ShouldNotBeEmpty();
        withExecutable.ShouldAllBe(e => ExecutablePath.IsUnder(e.ExecutablePath, e.InstallLocation));
        withExecutable.ShouldAllBe(e => File.Exists(e.ExecutablePath!));
    }

    [Fact]
    public async Task Signed_packages_carry_the_publisher_windows_verified()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        evidence.Where(e => e.SignatureStatus == "Signed").ShouldAllBe(e => e.SignerSubject != null && e.SignerSubject.Contains("CN=", StringComparison.OrdinalIgnoreCase));
        evidence.Where(e => e.SignatureStatus == null).ShouldAllBe(e => e.SignerSubject == null);
    }

    /// <summary>Package registrations are installation records: every one is a row.</summary>
    [Fact]
    public async Task Packages_are_rows()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        var rows = SoftwareDiscoveryPipeline.Run(evidence);

        rows.Count.ShouldBeGreaterThan(0);
        rows.ShouldAllBe(r => r.InstallLocation != null);
        ApplicationMerger.Merge(evidence).ShouldAllBe(a => a.Confidence == DiscoveryConfidence.Installed && a.Identity.Kind == IdentityKind.Package);
    }

    /// <summary>
    /// The acceptance case: Slack, a packaged desktop application with no
    /// uninstall key, reported as installed for the account it is deployed to,
    /// with the directory Force Stop would act on.
    /// </summary>
    [Fact]
    public async Task Slack_is_installed_where_it_is_deployed()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        var slack = evidence.FirstOrDefault(e => e.PackageFamilyName == "com.tinyspeck.slackdesktop_8yrtsj140pw4g"
            && string.Equals(e.InstalledForUser, CurrentAccount(), StringComparison.OrdinalIgnoreCase));
        if (slack is null)
        {
            // Not a Slack machine. The shape tests above still hold.
            return;
        }

        slack.Name.ShouldBe("Slack");
        slack.Publisher.ShouldBe("Slack Technologies Inc.");
        slack.Scope.ShouldBe(SoftwareScope.User);
        slack.InstallLocation.ShouldStartWith(@"C:\Program Files\WindowsApps\com.tinyspeck.slackdesktop_", Case.Insensitive);
        slack.ExecutablePath.ShouldNotBeNull();
        slack.ExecutablePath.ShouldEndWith(@"\app\Slack.exe", Case.Insensitive);
        slack.SignerSubject.ShouldNotBeNull().ShouldContain("Slack Technologies");
        slack.Category.ShouldBeNull();
        ApplicationProcessMatcher.CanResolve(slack.InstallLocation).ShouldBeTrue();

        SoftwareDiscoveryPipeline.Run(evidence).ShouldContain(r => r.Name == "Slack" && r.InstallationScope == "User");
    }

    [Theory]
    [InlineData(true, "1.0.0.0", "1.0.0.0", true)]
    [InlineData(false, "1.0.0.0", "1.0.0.0", false)]
    [InlineData(null, "1.0.0.0", "1.0.0.0", true)]
    [InlineData(null, "2.0.0.0", "1.0.0.0", false)]
    [InlineData(null, null, "1.0.0.0", true)]
    public void A_registration_is_deployed_by_the_index_or_failing_that_by_its_manifest(bool? inIndex, string? manifestVersion, string version, bool expected)
    {
        const string full = "Contoso.App_1.0.0.0_x64__abcdefghijklm";
        var index = inIndex is null ? null : new HashSet<string>(inIndex.Value ? [full] : [], StringComparer.OrdinalIgnoreCase);
        var manifest = manifestVersion is null ? null : new AppxManifest("Contoso.App", null, manifestVersion, null, null, null, false, false, null);

        WindowsPackageRegistrationEvidenceSource.IsDeployed(full, version, manifest, index).ShouldBe(expected);
    }
}
