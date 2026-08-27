using System.IO.Compression;
using System.Text;
using EndpointAgent.Core.Drivers;

namespace EndpointAgent.Core.Tests.Drivers;

/// <summary>
/// Unpacking a driver package archive.
///
/// This is the attack surface driver installation introduces that software
/// installation never had: the software path installs a single MSI and unpacks
/// nothing. Everything here is about an archive that lies — entries that climb out
/// of the extraction directory, entries that claim a drive root, and content that
/// expands far beyond what was sent.
///
/// The checks run against the resolved destination path rather than the entry name,
/// because name-based defences are what get bypassed: mixed separators, a sibling
/// directory whose name merely starts with the target's, and encoded traversal all
/// look harmless as strings.
/// </summary>
public sealed class DriverArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"epa-arch-test-{Guid.CreateVersion7():N}");

    public DriverArchiveTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test run over.
        }
    }

    private string Destination => Path.Combine(_root, "extracted");

    /// <summary>Builds a ZIP with exactly the entries given, including hostile names.</summary>
    private string CreateArchive(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(_root, $"pkg-{Guid.CreateVersion7():N}.zip");

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }

        return path;
    }

    // ---- the happy path ----------------------------------------------------

    [Fact]
    public void Extracts_a_well_formed_package_and_finds_its_inf()
    {
        var archive = CreateArchive(
            ("contoso.inf", "[Version]"),
            ("contoso.cat", "catalogue"),
            ("contoso.sys", "driver"));

        var outcome = DriverArchive.Extract(archive, Destination, "contoso.inf");

        outcome.Succeeded.ShouldBeTrue();
        outcome.Result.ShouldBe(DriverArchiveResult.Ok);
        outcome.EntryCount.ShouldBe(3);
        File.Exists(outcome.InfPath).ShouldBeTrue();
        Path.GetFileName(outcome.InfPath).ShouldBe("contoso.inf");
    }

    /// <summary>Vendors routinely ship the INF in an architecture subdirectory.</summary>
    [Fact]
    public void Finds_an_inf_nested_in_a_subdirectory()
    {
        var archive = CreateArchive(
            ("x64/contoso.inf", "[Version]"),
            ("x64/contoso.sys", "driver"));

        var outcome = DriverArchive.Extract(archive, Destination, "contoso.inf");

        outcome.Succeeded.ShouldBeTrue();
        outcome.InfPath.ShouldNotBeNull();
        File.Exists(outcome.InfPath).ShouldBeTrue();
    }

    [Fact]
    public void Refuses_a_package_that_does_not_contain_the_named_inf()
    {
        var archive = CreateArchive(("other.inf", "[Version]"));

        var outcome = DriverArchive.Extract(archive, Destination, "contoso.inf");

        outcome.Succeeded.ShouldBeFalse();
        outcome.Result.ShouldBe(DriverArchiveResult.InfNotFound);
        outcome.InfPath.ShouldBeNull();
    }

    // ---- hostile archives --------------------------------------------------

    /// <summary>
    /// Zip-slip. An entry that climbs out of the extraction directory would let an
    /// archive write anywhere the agent can — and the agent runs as LocalSystem.
    /// </summary>
    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("../../escaped.txt")]
    [InlineData("sub/../../escaped.txt")]
    [InlineData(@"..\escaped.txt")]
    public void Refuses_an_entry_that_escapes_the_extraction_directory(string name)
    {
        var archive = CreateArchive(("contoso.inf", "[Version]"), (name, "payload"));

        var outcome = DriverArchive.Extract(archive, Destination, "contoso.inf");

        outcome.Succeeded.ShouldBeFalse();
        outcome.Result.ShouldBe(DriverArchiveResult.PathTraversal);

        File.Exists(Path.Combine(_root, "escaped.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(Path.GetTempPath(), "escaped.txt")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("C:/windows/system32/evil.sys")]
    [InlineData(@"C:\windows\system32\evil.sys")]
    [InlineData("//server/share/evil.sys")]
    [InlineData(@"\\server\share\evil.sys")]
    [InlineData("/etc/passwd")]
    public void Refuses_an_entry_with_an_absolute_or_rooted_path(string name)
    {
        var archive = CreateArchive(("contoso.inf", "[Version]"), (name, "payload"));

        var outcome = DriverArchive.Extract(archive, Destination, "contoso.inf");

        outcome.Succeeded.ShouldBeFalse();
        outcome.Result.ShouldBeOneOf(DriverArchiveResult.AbsolutePath, DriverArchiveResult.PathTraversal);
    }

    /// <summary>
    /// A sibling directory whose name starts with the destination's. The case a
    /// naive prefix check on the path string gets wrong.
    /// </summary>
    [Fact]
    public void Refuses_an_entry_escaping_into_a_similarly_named_sibling_directory()
    {
        var archive = CreateArchive(
            ("contoso.inf", "[Version]"),
            ("../extracted-evil/payload.sys", "payload"));

        var outcome = DriverArchive.Extract(archive, Destination, "contoso.inf");

        outcome.Succeeded.ShouldBeFalse();
        outcome.Result.ShouldBe(DriverArchiveResult.PathTraversal);
        Directory.Exists(Path.Combine(_root, "extracted-evil")).ShouldBeFalse();
    }

    [Fact]
    public void Refuses_an_archive_with_too_many_entries()
    {
        var entries = Enumerable.Range(0, DriverArchive.MaxEntries + 1)
            .Select(i => ($"file{i}.bin", "x"))
            .ToArray();

        var outcome = DriverArchive.Extract(CreateArchive(entries), Destination, "contoso.inf");

        outcome.Succeeded.ShouldBeFalse();
        outcome.Result.ShouldBe(DriverArchiveResult.TooManyEntries);

        // Refused before writing anything: the count is known from the directory.
        outcome.EntryCount.ShouldBe(DriverArchive.MaxEntries + 1);
    }

    /// <summary>
    /// A zip bomb: a small archive whose declared expansion exceeds the ceiling. The
    /// check is made against the declared length before writing, so the disk is never
    /// filled to discover it.
    /// </summary>
    [Fact]
    public void Refuses_an_archive_that_expands_beyond_the_ceiling()
    {
        var path = Path.Combine(_root, "bomb.zip");

        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            // Highly compressible, and far past the limit once expanded.
            var block = new byte[8 * 1024 * 1024];

            for (var i = 0; i < 80; i++)
            {
                var entry = archive.CreateEntry($"pad{i}.bin", CompressionLevel.SmallestSize);
                using var target = entry.Open();
                target.Write(block);
            }
        }

        var outcome = DriverArchive.Extract(path, Destination, "contoso.inf");

        outcome.Succeeded.ShouldBeFalse();
        outcome.Result.ShouldBe(DriverArchiveResult.TooLarge);
        outcome.ExpandedBytes.ShouldBeLessThanOrEqualTo(DriverArchive.MaxExpandedBytes);
    }

    [Fact]
    public void Refuses_content_that_is_not_a_readable_archive()
    {
        var path = Path.Combine(_root, "not-a-zip.zip");
        File.WriteAllText(path, "this is not a zip file");

        var outcome = DriverArchive.Extract(path, Destination, "contoso.inf");

        outcome.Succeeded.ShouldBeFalse();
        outcome.Result.ShouldBe(DriverArchiveResult.Malformed);
    }

    /// <summary>
    /// A duplicated entry name must not overwrite what was already written, so a
    /// second entry cannot replace a verified file with different content.
    /// </summary>
    [Fact]
    public void Refuses_a_duplicate_entry_rather_than_overwriting()
    {
        var archive = CreateArchive(
            ("contoso.inf", "[Version]"),
            ("payload.sys", "first"),
            ("payload.sys", "second"));

        var outcome = DriverArchive.Extract(archive, Destination, "contoso.inf");

        outcome.Succeeded.ShouldBeFalse();
        outcome.Result.ShouldBe(DriverArchiveResult.Malformed);
    }

    /// <summary>
    /// The INF is located among the extracted files, so a name carrying a path
    /// cannot select something outside the extraction directory.
    /// </summary>
    [Theory]
    [InlineData("../../../windows/inf/usbstor.inf")]
    [InlineData(@"C:\Windows\INF\usbstor.inf")]
    public void An_inf_name_carrying_a_path_cannot_select_a_file_outside_the_package(string infName)
    {
        var archive = CreateArchive(("contoso.inf", "[Version]"));

        var outcome = DriverArchive.Extract(archive, Destination, infName);

        // Only the bare name is matched, and this package has no such file.
        outcome.Succeeded.ShouldBeFalse();
        outcome.Result.ShouldBe(DriverArchiveResult.InfNotFound);
    }
}
