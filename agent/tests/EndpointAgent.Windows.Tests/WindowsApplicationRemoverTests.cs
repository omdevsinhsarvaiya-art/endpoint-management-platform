using EndpointAgent.Core.Abstractions;
using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Xunit.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// Windows-only checks for the application remover that are safe on a real
/// machine: refusals, the idempotent "already gone" answers, and one trip through
/// the whole deployment-engine chain for a package that does not exist.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here removes anything. A random product code is not installed, so the
/// Windows Installer path ends at the read-only state query, and a package name
/// nobody has ends at the registry, before the engine is asked. The engine
/// itself is driven directly, with names that cannot name anything, so that it
/// activates, takes an HSTRING, starts an operation, finishes it and hands back
/// a result -- every step of the interop -- with nothing to act on. The only
/// real product named is the agent's own, which the remover must refuse before
/// any call that changes state; that this test would uninstall the agent if the
/// refusal were wrong is the point of running it.
/// </para>
/// <para>
/// The self-protection and package-name rules are also proven as pure functions,
/// because a machine without the agent installed cannot prove the composed path
/// and a test that removed a real product to prove a rule would be worse than
/// no test.
/// </para>
/// </remarks>
public sealed class WindowsApplicationRemoverTests(ITestOutputHelper output)
{
    private const string AgentDir = @"C:\Program Files\EndpointPlatform\Agent";

    /// <summary>A product code that is not the agent's, for the identity rules.</summary>
    private const string OtherProductCode = "{2C1F6A3E-9B47-4D0A-8E51-3F2A7B9C4D60}";

    private const uint ErrorSuccess = 0;
    private const uint ErrorNoMoreItems = 259;

    private static WindowsApplicationRemover Create(string? agentDirectory = null) =>
        new(NullLogger<WindowsApplicationRemover>.Instance, agentDirectory);

    private static WindowsMsiPackageInstaller Installer() =>
        new(NullLogger<WindowsMsiPackageInstaller>.Instance);

    // ------------------------------------------------------- Windows Installer

    [Fact]
    public async Task A_random_product_code_is_already_removed_and_nothing_changes()
    {
        // Astronomically unlikely to be present: the path ends at the real,
        // read-only MsiQueryProductState and reports the desired state as holding.
        var productCode = Guid.CreateVersion7().ToString("B").ToUpperInvariant();

        var outcome = await Create().RemoveWindowsInstallerProductAsync(productCode);

        outcome.Result.ShouldBe(ApplicationRemovalResult.AlreadyRemoved);
        outcome.Succeeded.ShouldBeTrue();
        (await Installer().IsProductInstalledAsync(productCode)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("2C1F6A3E-9B47-4D0A-8E51-3F2A7B9C4D60")]
    [InlineData("{2C1F6A3E-9B47-4D0A-8E51-3F2A7B9C4D60}x")]
    [InlineData("{2C1F6A3E-9B47-4D0A-8E51-3F2A7B9C4D60} REBOOT=Force")]
    [InlineData(@"C:\Windows\Installer\1a2b3c.msi")]
    public async Task A_malformed_product_code_is_refused_before_windows_installer_is_asked(string productCode)
    {
        var outcome = await Create().RemoveWindowsInstallerProductAsync(productCode);

        outcome.Result.ShouldBe(ApplicationRemovalResult.Refused);
        outcome.Succeeded.ShouldBeFalse();
        outcome.Code.ShouldBeNull();
    }

    /// <summary>
    /// The agent's own product, found the way an administrator would find it --
    /// by display name in the uninstall registry -- is refused, and is still
    /// installed afterwards. On a machine without the agent there is nothing to
    /// name; the pure rule below covers the logic.
    /// </summary>
    [Fact]
    public async Task The_agents_own_product_is_refused_and_stays_installed()
    {
        var productCode = FindProductCodeByDisplayName(WindowsApplicationRemover.AgentProductName);
        if (productCode is null)
        {
            return; // The agent is not installed on this machine; nothing to refuse.
        }

        var installer = Installer();
        var before = await installer.IsProductInstalledAsync(productCode);

        var outcome = await Create().RemoveWindowsInstallerProductAsync(productCode);

        outcome.Result.ShouldBe(ApplicationRemovalResult.Refused);
        outcome.Detail!.ShouldContain("endpoint agent");
        (await installer.IsProductInstalledAsync(productCode)).ShouldBe(before, "the refusal must change nothing");
    }

    [Theory]
    [InlineData("Endpoint Platform Agent", null, true)]
    [InlineData("endpoint platform agent", null, true)]
    [InlineData(" Endpoint Platform Agent ", null, true)]
    [InlineData(null, AgentDir, true)]
    [InlineData(null, AgentDir + @"\", true)]
    [InlineData(null, @"c:\program files\endpointplatform\agent", true)]
    [InlineData(null, @"C:\Program Files\EndpointPlatform", true)]
    [InlineData(null, @"C:\Program Files\EndpointPlatform\Agent\bin", true)]
    [InlineData("Contoso Reader", @"C:\Program Files\Contoso\Reader", false)]
    [InlineData("Endpoint Platform Agent Helper", @"C:\Program Files\Contoso", false)]
    [InlineData(null, @"C:\Program Files\EndpointPlatformAgent", false)]
    [InlineData(null, null, false)]
    public void A_product_is_the_agent_by_name_or_by_directory(string? name, string? location, bool protectedProduct)
    {
        var refusal = WindowsApplicationRemover.ProtectedProductRefusal(name, location, AgentDir);

        (refusal is not null).ShouldBe(protectedProduct);
    }

    /// <summary>
    /// The directory rule reaches the agent's own folder, anything inside it and
    /// the vendor folder that holds it -- and stops there. Protecting every
    /// ancestor would make <c>C:\Program Files</c> and <c>C:\</c> protected
    /// directories, and a driver or utility that registers one of those as its
    /// <c>InstallLocation</c> -- they do -- could never be removed, with a
    /// reason that claims it is the agent's directory when it is not.
    /// </summary>
    [Theory]
    [InlineData(AgentDir, true)]
    [InlineData(AgentDir + @"\resources\en-GB", true)]
    [InlineData(@"C:\Program Files\EndpointPlatform", true)]
    [InlineData(@"C:\Program Files", false)]
    [InlineData(@"C:\Program Files\", false)]
    [InlineData(@"C:\", false)]
    [InlineData("C:", false)]
    [InlineData(@"D:\Utilities\Contoso", false)]
    public void The_directory_rule_stops_at_the_vendor_folder(string location, bool protectedProduct)
    {
        var refusal = WindowsApplicationRemover.ProtectedProductRefusal(
            "Contoso Widget Driver", location, AgentDir);

        (refusal is not null).ShouldBe(protectedProduct);
    }

    // ------------------------------------------------- upgrade-code identity

    /// <summary>
    /// The enumeration reaching its end -- ERROR_NO_MORE_ITEMS -- is the only
    /// status that clears a product, and a product it lists is the agent.
    /// </summary>
    [Fact]
    public void A_product_listed_under_the_agents_upgrade_code_is_the_agent()
    {
        var membership = WindowsApplicationRemover.RelatedProductMembership(
            OtherProductCode,
            index => index switch
            {
                0 => (ErrorSuccess, "{1F0E4C7A-2D63-4B85-9A0F-6E3D8C15B742}"),
                1 => (ErrorSuccess, OtherProductCode.ToLowerInvariant()),
                _ => (ErrorNoMoreItems, null),
            });

        membership.ShouldBe(WindowsApplicationRemover.UpgradeCodeMembership.Agent);
    }

    [Fact]
    public void A_product_the_enumeration_runs_past_is_not_the_agent()
    {
        var membership = WindowsApplicationRemover.RelatedProductMembership(
            OtherProductCode,
            index => index == 0
                ? (ErrorSuccess, "{1F0E4C7A-2D63-4B85-9A0F-6E3D8C15B742}")
                : (ErrorNoMoreItems, null));

        membership.ShouldBe(WindowsApplicationRemover.UpgradeCodeMembership.NotAgent);
    }

    /// <summary>
    /// Any other status means the question was not answered. Self-protection
    /// fails closed: "Windows Installer could not tell me" is not "not the
    /// agent", and treating it as one is how an agent uninstalls itself on the
    /// single machine whose Installer configuration is broken.
    /// </summary>
    [Theory]
    [InlineData(1610u)] // ERROR_BAD_CONFIGURATION
    [InlineData(87u)]   // ERROR_INVALID_PARAMETER
    [InlineData(8u)]    // ERROR_NOT_ENOUGH_MEMORY
    [InlineData(1601u)] // ERROR_INSTALL_SERVICE_FAILURE
    public void An_enumeration_error_leaves_the_products_identity_unconfirmed(uint status)
    {
        var membership = WindowsApplicationRemover.RelatedProductMembership(
            OtherProductCode, _ => (status, null));

        membership.ShouldBe(WindowsApplicationRemover.UpgradeCodeMembership.Unconfirmed);
    }

    /// <summary>
    /// An enumeration that never ends is not an answer either: the agent's
    /// upgrade code has one product, so a machine that keeps producing them is
    /// telling the same story a status code would.
    /// </summary>
    [Fact]
    public void An_enumeration_that_never_ends_leaves_the_identity_unconfirmed()
    {
        var membership = WindowsApplicationRemover.RelatedProductMembership(
            OtherProductCode, index => (ErrorSuccess, $"{{{index:D8}-0000-0000-0000-000000000000}}"));

        membership.ShouldBe(WindowsApplicationRemover.UpgradeCodeMembership.Unconfirmed);
    }

    /// <summary>
    /// Each verdict becomes the refusal the operator sees. An unconfirmed
    /// identity refuses a product that passes every other check -- name,
    /// directory, everything -- because the check that matters did not run.
    /// </summary>
    [Fact]
    public void An_unconfirmed_identity_refuses_a_product_that_is_otherwise_removable()
    {
        var refusal = WindowsApplicationRemover.ProtectedProductRefusal(
            "Contoso Reader",
            @"C:\Program Files\Contoso\Reader",
            AgentDir,
            WindowsApplicationRemover.UpgradeCodeMembership.Unconfirmed);

        refusal.ShouldBe("refused: Windows Installer could not confirm the product's identity");
    }

    [Fact]
    public void A_product_under_the_agents_upgrade_code_is_refused_whatever_it_calls_itself()
    {
        var refusal = WindowsApplicationRemover.ProtectedProductRefusal(
            "Contoso Reader",
            @"C:\Program Files\Contoso\Reader",
            AgentDir,
            WindowsApplicationRemover.UpgradeCodeMembership.Agent);

        refusal!.ShouldContain("this is the endpoint agent");
    }

    /// <summary>
    /// The identity read is the real MsiGetProductInfo, proven against a product
    /// that is genuinely installed: a wrong entry point or a wrong buffer
    /// protocol would read nothing, and a self-protection check that reads
    /// nothing protects nothing.
    /// </summary>
    [Fact]
    public void A_real_products_name_is_read_from_windows_installer()
    {
        var productCode = FindAnInstalledMsiProductCode();
        if (productCode is null)
        {
            return; // No per-machine MSI product on this machine; nothing to read.
        }

        var name = WindowsApplicationRemover.ReadProductProperty(productCode, "ProductName")
            ?? WindowsApplicationRemover.ReadProductProperty(productCode, "InstalledProductName");

        name.ShouldNotBeNullOrWhiteSpace($"{productCode} is installed and must report a name");
    }

    [Fact]
    public void An_unknown_product_has_no_readable_properties()
    {
        var productCode = Guid.CreateVersion7().ToString("B").ToUpperInvariant();

        WindowsApplicationRemover.ReadProductProperty(productCode, "ProductName").ShouldBeNull();
        WindowsApplicationRemover.ReadProductProperty(productCode, "InstallLocation").ShouldBeNull();
    }

    // ---------------------------------------------------------------- packages

    [Theory]
    [InlineData("")]
    [InlineData("Contoso.App")]
    [InlineData("Contoso.App_1.0.0.0_x64")]
    [InlineData("Contoso.App_1.0.0.0_x64__abc")]
    [InlineData("Contoso.App__x64__abcdefghijklm")]
    [InlineData("Contoso.App_1.0.0.0___abcdefghijklm")]
    [InlineData("Contoso App_1.0.0.0_x64__abcdefghijklm")]
    [InlineData(@"Contoso.App_1.0.0.0_x64__abcdefghijklm\..")]
    [InlineData("Contoso.App_1.0.0.0_x64__abcdefghijklm/x")]
    [InlineData("Contoso.App_1.0.0.0_x64__abcdefghijklm_extra")]
    public async Task A_malformed_package_full_name_is_refused(string packageFullName)
    {
        var outcome = await Create().RemovePackageAsync(packageFullName);

        outcome.Result.ShouldBe(ApplicationRemovalResult.Refused);
        outcome.Detail!.ShouldContain("malformed");
    }

    [Theory]
    [InlineData("Microsoft.Windows.ShellExperienceHost_10.0.26100.1_neutral_neutral_cw5n1h2txyewy")]
    [InlineData("MicrosoftWindows.Client.CBS_1000.26100.1.0_x64__CW5N1H2TXYEWY")]
    public async Task A_windows_inbox_package_is_refused(string packageFullName)
    {
        var outcome = await Create().RemovePackageAsync(packageFullName);

        outcome.Result.ShouldBe(ApplicationRemovalResult.Refused);
        outcome.Detail!.ShouldContain("operating-system");
    }

    [Theory]
    [InlineData(@"C:\Windows\SystemApps\Contoso_abcdefghijklm", true)]
    [InlineData(@"C:\Windows", true)]
    [InlineData(@"c:\windows\", true)]
    [InlineData(@"C:\Program Files\WindowsApps\Contoso.App_1.0.0.0_x64__abcdefghijklm", false)]
    [InlineData(@"C:\WindowsApps\Contoso", false)]
    [InlineData(null, false)]
    public void A_package_root_under_the_windows_directory_is_a_system_component(string? root, bool protectedPackage)
    {
        WindowsApplicationRemover.IsUnderWindowsDirectory(root, @"C:\Windows").ShouldBe(protectedPackage);
    }

    /// <summary>
    /// A well-formed name that no repository and no index lists is already the
    /// desired state, and is reported so from the registry alone: the engine
    /// completes a removal of a package nobody has as a success, which is the
    /// wrong word for it.
    /// </summary>
    [Theory]
    [InlineData("Contoso.Nothing_1.0.0.0_x64__abcdefghijklm")]
    [InlineData("Contoso.Nothing_1.0.0.0_x64__0123456789abc")]
    public async Task A_package_nobody_has_is_already_removed(string packageFullName)
    {
        var outcome = await Create().RemovePackageAsync(packageFullName);

        outcome.Result.ShouldBe(ApplicationRemovalResult.AlreadyRemoved);
        outcome.Succeeded.ShouldBeTrue();
        outcome.Code.ShouldBeNull();
        outcome.Detail!.ShouldContain("not registered");
    }

    /// <summary>
    /// The whole chain, live: Windows Runtime initialisation, activation of the
    /// package manager, an HSTRING, IPackageManager2 through the hand-declared
    /// vtable, the asynchronous wait on IAsyncInfo, GetResults and the
    /// deployment result. The name has a publisher id outside the package base32
    /// alphabet (no i, l, o or u), so the engine rejects it as an invalid
    /// argument: a non-zero HRESULT, with the engine's own sentence about it
    /// arriving through the result's ErrorText. A wrong slot anywhere would not
    /// produce an HRESULT and a sentence; nothing exists for it to act on.
    /// </summary>
    [Fact]
    public async Task The_deployment_engine_answers_a_name_it_cannot_accept_with_its_own_words()
    {
        var report = await WindowsPackageManager.RemoveForAllUsersAsync(
            "Contoso.Nothing_1.0.0.0_x64__abcdefghijklm", TimeSpan.FromMinutes(1), CancellationToken.None);

        output.WriteLine($"Completed={report.Completed} 0x{report.Code:X8}: {report.ErrorText}");

        report.Completed.ShouldBeFalse();
        report.Code.ShouldBe(unchecked((int)0x80070057), "E_INVALIDARG for a publisher id outside the alphabet");
        report.ErrorText.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The same chain to a completed operation: a name that is well-formed in
    /// every respect and simply not there. What the engine says for it depends
    /// on the engine -- ERROR_INSTALL_PACKAGE_NOT_FOUND, or a completed no-op --
    /// and the remover does not rely on either, which is why it checks the
    /// registry first. Both are recorded here so a change in the engine's answer
    /// is noticed.
    /// </summary>
    [Fact]
    public async Task The_deployment_engine_finishes_an_operation_for_a_name_nobody_has()
    {
        var report = await WindowsPackageManager.RemoveForAllUsersAsync(
            "Contoso.Nothing_1.0.0.0_x64__0123456789abc", TimeSpan.FromMinutes(1), CancellationToken.None);

        output.WriteLine($"Completed={report.Completed} 0x{report.Code:X8}: {report.ErrorText}");

        var notFound = report.Code == unchecked((int)0x80073CF1);
        var completedNoOp = report.Completed && report.Code == 0;
        (notFound || completedNoOp).ShouldBeTrue(
            $"expected not-found or a completed no-op, got Completed={report.Completed} 0x{report.Code:X8}");
    }

    // ------------------------------------------------------------- registry

    private static string? FindProductCodeByDisplayName(string displayName)
    {
        foreach (var root in UninstallRoots)
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key is null)
            {
                continue;
            }

            foreach (var name in key.GetSubKeyNames())
            {
                if (!Guid.TryParse(name, out _))
                {
                    continue;
                }

                using var sub = key.OpenSubKey(name);
                if (string.Equals(sub?.GetValue("DisplayName") as string, displayName, StringComparison.Ordinal))
                {
                    return name;
                }
            }
        }

        return null;
    }

    private static string? FindAnInstalledMsiProductCode()
    {
        foreach (var root in UninstallRoots)
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key is null)
            {
                continue;
            }

            foreach (var name in key.GetSubKeyNames())
            {
                if (!Guid.TryParse(name, out _))
                {
                    continue;
                }

                using var sub = key.OpenSubKey(name);
                if (sub?.GetValue("WindowsInstaller") is int wi && wi == 1)
                {
                    return name;
                }
            }
        }

        return null;
    }

    private static readonly string[] UninstallRoots =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];
}
