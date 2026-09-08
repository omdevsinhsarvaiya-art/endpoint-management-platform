using EndpointAgent.Core.Inventory;
using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// The Start Menu, read from this machine's real folders.
/// </summary>
/// <remarks>
/// Asserts the shape every Windows machine satisfies -- the all-users Programs
/// folder always holds shortcuts to local executables -- and never a particular
/// application. What a shortcut is worth is decided by the merger and proven
/// with fixtures; what this proves is that every real shortcut here either
/// yields a usable executable or is refused, and that none of them throws.
/// </remarks>
public sealed class WindowsStartMenuEvidenceSourceTests
{
    private static WindowsStartMenuEvidenceSource Create() =>
        new(NullLogger<WindowsStartMenuEvidenceSource>.Instance);

    private static readonly IReadOnlyDictionary<string, string> NoVariables = new Dictionary<string, string>();

    [Fact]
    public async Task Reports_shortcuts_from_the_all_users_programs_folder()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        evidence.ShouldContain(e => e.Scope == SoftwareScope.Machine);
        evidence.ShouldAllBe(e => e.Source == EvidenceSource.StartMenuShortcut);
    }

    [Fact]
    public async Task Every_target_is_an_existing_local_executable()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        evidence.ShouldAllBe(e => e.ExecutablePath != null && ExecutablePath.Normalize(e.ExecutablePath) == e.ExecutablePath);
        evidence.ShouldAllBe(e => e.ExecutablePath!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        evidence.ShouldAllBe(e => File.Exists(e.ExecutablePath!));
    }

    [Fact]
    public async Task Every_shortcut_is_named_and_never_claims_an_installation()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        evidence.ShouldAllBe(e => !string.IsNullOrWhiteSpace(e.Name));
        evidence.ShouldAllBe(e => !e.IsAuthoritative);
        evidence.ShouldAllBe(e => e.ProductCode == null && e.PackageFamilyName == null && e.InstallLocation == null);
    }

    [Fact]
    public async Task User_shortcuts_carry_their_account_and_machine_shortcuts_do_not()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        evidence.Where(e => e.Scope == SoftwareScope.Machine).ShouldAllBe(e => e.InstalledForUser == null);
        evidence.Where(e => e.Scope == SoftwareScope.User).ShouldAllBe(e => !string.IsNullOrWhiteSpace(e.InstalledForUser));
    }

    /// <summary>Through the whole pipeline, shortcuts alone put nothing in the report.</summary>
    [Fact]
    public async Task Shortcuts_alone_produce_no_inventory_rows()
    {
        var evidence = await Create().CollectEvidenceAsync(CancellationToken.None);

        SoftwareDiscoveryPipeline.Run(evidence).ShouldBeEmpty();
    }

    /// <summary>
    /// Every real shortcut on this machine goes through the parser: none throws,
    /// and the ones Windows wrote for installed programs decode to a local path.
    /// </summary>
    [Fact]
    public void Every_real_shortcut_parses_or_is_refused_without_throwing()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        var shortcuts = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToList();
        shortcuts.ShouldNotBeEmpty();

        var parsed = 0;
        foreach (var shortcut in shortcuts)
        {
            var link = Should.NotThrow(() => ShellLink.Parse(File.ReadAllBytes(shortcut)));
            if (link is not null)
            {
                parsed++;
            }
        }

        parsed.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void A_file_that_is_not_a_shortcut_yields_no_target()
    {
        var path = Path.Combine(Path.GetTempPath(), $"not-a-shortcut-{Guid.NewGuid():N}.lnk");
        File.WriteAllText(path, "this is text, not a shell link");
        try
        {
            WindowsStartMenuEvidenceSource.Target(path, NoVariables).ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_oversized_file_is_not_read()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oversized-{Guid.NewGuid():N}.lnk");
        File.WriteAllBytes(path, new byte[WindowsStartMenuEvidenceSource.MaxShortcutBytes + 1]);
        try
        {
            WindowsStartMenuEvidenceSource.Target(path, NoVariables).ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_missing_shortcut_yields_no_target()
    {
        WindowsStartMenuEvidenceSource.Target(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.lnk"), NoVariables)
            .ShouldBeNull();
    }

    /// <summary>
    /// A real shortcut, read through the source's own path: the target it yields
    /// is the file the shortcut names, normalised.
    /// </summary>
    [Fact]
    public void A_real_shortcut_yields_its_normalised_target()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        var machine = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows",
            ["ProgramFiles"] = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files",
        };

        var targets = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories)
            .Select(f => WindowsStartMenuEvidenceSource.Target(f, machine))
            .Where(t => t is not null)
            .ToList();

        targets.ShouldNotBeEmpty();
        targets.ShouldAllBe(t => ExecutablePath.Normalize(t) == t && File.Exists(t!));
    }
}
