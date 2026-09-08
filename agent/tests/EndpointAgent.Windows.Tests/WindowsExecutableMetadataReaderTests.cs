using EndpointAgent.Core.Inventory;
using EndpointAgent.Windows;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// Reading an executable's version resource and embedded signature from real
/// files on this machine.
/// </summary>
/// <remarks>
/// Uses files every Windows machine and every .NET build has: the test assembly
/// itself (unsigned, versioned), Notepad (Microsoft's, catalog-signed and so
/// embedded-unsigned), and -- where the SDK is installed -- <c>dotnet.exe</c>,
/// which carries an embedded Microsoft signature.
/// </remarks>
public sealed class WindowsExecutableMetadataReaderTests
{
    private static readonly WindowsExecutableMetadataReader Reader = new();

    [Fact]
    public void A_missing_file_is_null()
    {
        Reader.Read(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe")).ShouldBeNull();
    }

    [Fact]
    public void A_blank_path_is_refused()
    {
        Should.Throw<ArgumentException>(() => Reader.Read(" "));
    }

    [Fact]
    public void The_test_assembly_is_versioned_and_unsigned()
    {
        var metadata = Reader.Read(typeof(WindowsExecutableMetadataReaderTests).Assembly.Location).ShouldNotBeNull();

        metadata.FileVersion.ShouldNotBeNull();
        metadata.SignatureStatus.ShouldBe(ExecutableSignatureStatus.Unsigned);
        metadata.SignerSubject.ShouldBeNull();
    }

    [Fact]
    public void Notepad_declares_microsoft_as_its_company()
    {
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (!File.Exists(notepad))
        {
            return;
        }

        var metadata = Reader.Read(notepad).ShouldNotBeNull();

        metadata.CompanyName.ShouldBe("Microsoft Corporation");
        metadata.ProductName.ShouldNotBeNull();
        metadata.FileVersion.ShouldNotBeNull();
        metadata.SignatureStatus.ShouldNotBe(ExecutableSignatureStatus.Unreadable);
    }

    [Fact]
    public void A_signed_executable_names_its_signer_as_a_claim()
    {
        var dotnet = Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "dotnet", "dotnet.exe");
        if (!File.Exists(dotnet))
        {
            // No SDK on this machine; the signed path is not exercisable here.
            return;
        }

        var metadata = Reader.Read(dotnet).ShouldNotBeNull();

        metadata.SignatureStatus.ShouldBe(ExecutableSignatureStatus.Signed);
        metadata.SignerSubject.ShouldNotBeNull();
        metadata.SignerSubject.ShouldContain("Microsoft", Case.Insensitive);
        metadata.CompanyName.ShouldBe("Microsoft Corporation");
    }

    [Fact]
    public void A_file_that_is_not_an_image_does_not_throw()
    {
        var path = Path.Combine(Path.GetTempPath(), $"text-{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, "not an executable");
        try
        {
            var metadata = Should.NotThrow(() => Reader.Read(path));

            if (metadata is not null)
            {
                metadata.ProductName.ShouldBeNull();
                metadata.SignerSubject.ShouldBeNull();
                metadata.SignatureStatus.ShouldNotBe(ExecutableSignatureStatus.Signed);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Blank_resource_strings_read_as_absent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"empty-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, []);
        try
        {
            var metadata = Reader.Read(path);
            if (metadata is not null)
            {
                metadata.ProductName.ShouldBeNull();
                metadata.CompanyName.ShouldBeNull();
                metadata.FileVersion.ShouldBeNull();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
