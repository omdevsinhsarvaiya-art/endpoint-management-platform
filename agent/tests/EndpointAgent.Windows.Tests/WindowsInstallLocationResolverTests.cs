using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// Recovering an install directory for the applications whose uninstall key
/// omits one.
/// </summary>
/// <remarks>
/// <para>
/// This is what decides whether Force Stop is offered at all, so the tests are
/// weighted towards what it refuses. A wrong directory here is not a cosmetic
/// error: it becomes the root the matcher terminates processes under.
/// </para>
/// <para>
/// The DisplayIcon cases use real files on disk because the resolver
/// deliberately requires the target to exist -- a stale pointer to something
/// uninstalled is one of the things it filters out, and a test that stubbed the
/// filesystem would not exercise that.
/// </para>
/// </remarks>
public sealed class WindowsInstallLocationResolverTests : IDisposable
{
    // Deliberately NOT under %TEMP%: the resolver treats a path containing
    // "\Temp\" as an installer cache and refuses it, so fixtures placed there
    // would be rejected for the wrong reason and prove nothing.
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory, "epp-resolver-" + Guid.NewGuid().ToString("n"));

    private readonly WindowsInstallLocationResolver _resolver =
        new(NullLogger<WindowsInstallLocationResolver>.Instance, agentDirectory: @"C:\Agent\Home");

    public WindowsInstallLocationResolverTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A fixture directory that outlives the test is not a test failure.
        }
    }

    private string CreateExe(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [0x4d, 0x5a]);
        return path;
    }

    [Fact]
    public void Accepts_a_display_icon_pointing_at_a_real_executable()
    {
        var exe = CreateExe("app.exe");

        Assert.Equal(_root, _resolver.FromDisplayIcon(exe));
    }

    [Theory]
    [InlineData(",0")]
    [InlineData(",1")]
    [InlineData(",-101")]
    public void Strips_the_icon_index(string suffix)
    {
        var exe = CreateExe("app.exe");

        Assert.Equal(_root, _resolver.FromDisplayIcon(exe + suffix));
    }

    [Fact]
    public void Accepts_a_quoted_display_icon()
    {
        var exe = CreateExe("app.exe");

        Assert.Equal(_root, _resolver.FromDisplayIcon("\"" + exe + "\""));
    }

    [Fact]
    public void Rejects_an_executable_that_does_not_exist()
    {
        // Uninstalled or moved since the uninstall key was written.
        Assert.Null(_resolver.FromDisplayIcon(Path.Combine(_root, "missing.exe")));
    }

    [Theory]
    [InlineData("icon.ico")]
    [InlineData("resources.dll")]
    public void Rejects_anything_that_is_not_an_executable(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [0x00]);

        // An icon file or a resource DLL says nothing about which process is the
        // application.
        Assert.Null(_resolver.FromDisplayIcon(path));
    }

    /// <remarks>
    /// Measured on the reference machine, four of the six applications that had
    /// a DisplayIcon at all pointed at a cached installer rather than at the
    /// application. Accepting those would make the installer cache an
    /// application's install directory, and Force Stop would then target
    /// whatever else happened to be running from there.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\ProgramData\Package Cache\{1234}\VC_redist.x64.exe")]
    [InlineData(@"C:\Windows\Installer\{5678}\setup.exe")]
    [InlineData(@"C:\Users\someone\Downloads\python-3.14.7-amd64.exe")]
    [InlineData(@"C:\Windows\Temp\dotnet-sdk-win-x64.exe")]
    public void Rejects_an_installer_cache_path(string displayIcon)
    {
        Assert.Null(_resolver.FromDisplayIcon(displayIcon));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_an_absent_display_icon(string? displayIcon)
    {
        Assert.Null(_resolver.FromDisplayIcon(displayIcon));
    }

    [Fact]
    public void Resolves_nothing_when_there_is_no_product_code_and_no_icon()
    {
        Assert.Null(_resolver.Resolve(null, null));
    }

    /// <remarks>
    /// The agent is an installed application and resolves like one, so without
    /// this the console would offer Force Stop on the agent itself. The matcher
    /// refuses to act on it, but an operator should not be shown a button that
    /// is guaranteed to fail.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\Agent\Home")]
    [InlineData(@"C:\Agent\Home\plugins")]
    [InlineData(@"C:\Agent")]
    [InlineData(@"c:\agent\home")]
    public void Never_reports_the_agents_own_directory(string directory)
    {
        // Asserted against Accept, not Resolve: driving this through a
        // DisplayIcon would have File.Exists return false for a made-up path and
        // the test would pass without the guard ever running.
        Assert.Null(_resolver.Accept(directory));
    }

    /// <remarks>
    /// A location the matcher would refuse must not be stored either. Reporting
    /// <c>C:\Program Files</c> would show as resolved in inventory while Force
    /// Stop could never act on it -- and the operator would have no way to tell
    /// that apart from a genuine failure.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files (x86)")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\ProgramData")]
    public void Never_reports_a_root_the_matcher_refuses(string root)
    {
        Assert.Null(_resolver.Accept(root));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Contoso")]
    [InlineData(@"C:\Users\someone\AppData\Local\Programs\contoso")]
    [InlineData(@"C:\Agent\HomeExtra")]
    public void Reports_a_genuine_install_directory(string directory)
    {
        // The counterpart to the refusals above: Accept must not be trivially
        // null-returning, or every test around it would pass for free.
        Assert.Equal(directory, _resolver.Accept(directory));
    }

    [Fact]
    public void Reports_a_directory_that_merely_looks_similar_to_the_agents()
    {
        // "…\HomeExtra" is not "…\Home". A prefix test without a separator would
        // wrongly withhold this one.
        var dir = Path.Combine(_root, "HomeExtra");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "app.exe");
        File.WriteAllBytes(exe, [0x4d, 0x5a]);

        var resolver = new WindowsInstallLocationResolver(
            NullLogger<WindowsInstallLocationResolver>.Instance,
            agentDirectory: Path.Combine(_root, "Home"));

        Assert.Equal(dir, resolver.Resolve(null, exe));
    }
}

/// <summary>
/// Reducing the files Windows Installer recorded for a product to one directory.
/// </summary>
public sealed class InstallComponentCommonDirectoryTests
{
    [Fact]
    public void Uses_the_directory_shared_by_every_file()
    {
        string[] paths =
        [
            @"C:\Program Files\Contoso\app.exe",
            @"C:\Program Files\Contoso\lib\core.dll",
            @"C:\Program Files\Contoso\docs\readme.txt",
        ];

        Assert.Equal(@"C:\Program Files\Contoso", WindowsInstallLocationResolver.CommonDirectory(paths));
    }

    /// <remarks>
    /// Why the comparison is by segment and not by character: a character prefix
    /// of "…\Contoso" and "…\ContosoExtra" is "…\Contoso", which is a real
    /// directory the product does not own. Force Stop would then reach into a
    /// neighbouring application.
    /// </remarks>
    [Fact]
    public void Does_not_invent_a_directory_from_a_shared_name_prefix()
    {
        string[] paths =
        [
            @"C:\Program Files\Contoso\app.exe",
            @"C:\Program Files\ContosoExtra\plugin.dll",
        ];

        // Their real common ancestor is the bare Program Files root. What matters
        // is that it is not "…\Contoso": the two products stay separated, and the
        // root itself is refused before it reaches inventory (below).
        Assert.Equal(@"C:\Program Files", WindowsInstallLocationResolver.CommonDirectory(paths));
    }

    [Fact]
    public void Refuses_a_product_whose_files_span_a_drive_root()
    {
        string[] paths =
        [
            @"C:\Program Files\Contoso\app.exe",
            @"C:\Windows\System32\driver.sys",
        ];

        Assert.Null(WindowsInstallLocationResolver.CommonDirectory(paths));
    }

    [Fact]
    public void Refuses_when_a_drive_letter_is_all_that_is_shared()
    {
        string[] paths = [@"C:\Alpha\a.exe", @"C:\Beta\b.exe"];

        Assert.Null(WindowsInstallLocationResolver.CommonDirectory(paths));
    }

    [Fact]
    public void Keeps_the_deepest_directory_for_a_single_file_product()
    {
        string[] paths = [@"C:\Program Files\Contoso\Tool\tool.exe"];

        Assert.Equal(@"C:\Program Files\Contoso\Tool", WindowsInstallLocationResolver.CommonDirectory(paths));
    }

    [Fact]
    public void Ignores_casing_differences_between_recorded_paths()
    {
        string[] paths =
        [
            @"C:\Program Files\Contoso\app.exe",
            @"c:\program files\contoso\lib\core.dll",
        ];

        Assert.Equal(@"C:\Program Files\Contoso", WindowsInstallLocationResolver.CommonDirectory(paths));
    }

    [Fact]
    public void Resolves_nothing_from_no_files()
    {
        Assert.Null(WindowsInstallLocationResolver.CommonDirectory([]));
    }
}
