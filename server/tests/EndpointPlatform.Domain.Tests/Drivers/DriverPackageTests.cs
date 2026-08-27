using EndpointPlatform.Domain.Drivers;

namespace EndpointPlatform.Domain.Tests.Drivers;

/// <summary>
/// What a driver package must be before it can exist.
///
/// The constructor is the narrowest place to enforce these, so a row that reaches
/// the database has already satisfied them however it got there. The one that
/// carries real weight is the signer pin: it is non-nullable, which means no code
/// path downstream ever has to decide what to do about a package that does not name
/// a publisher.
/// </summary>
public sealed class DriverPackageTests
{
    private const string ValidSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static DriverPackage Create(
        string sha = ValidSha,
        string fileName = "contoso-nic.zip",
        string infFileName = "contoso.inf",
        string hardwareId = @"PCI\VEN_8086&DEV_1234",
        string signer = "Contoso Corporation",
        long size = 4096) =>
        new(Guid.CreateVersion7(), "Contoso NIC", "2.0", "Contoso", sha, fileName, size,
            infFileName, hardwareId, "2.0.0.0", signer, Guid.CreateVersion7(), "admin@test");

    [Fact]
    public void A_well_formed_package_is_accepted_and_normalised()
    {
        var package = Create(sha: ValidSha.ToUpperInvariant());

        package.Sha256.ShouldBe(ValidSha, "the hash is stored lowercase so comparisons are ordinal");
        package.IsWithdrawn.ShouldBeFalse();
        package.RequiredSignerSubject.ShouldBe("Contoso Corporation");
    }

    // ---- the signer pin ----------------------------------------------------

    /// <summary>
    /// The decision that separates a driver package from a software package. A
    /// trusted signature alone is not enough for kernel code, so a package that does
    /// not name its publisher cannot be created at all.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_package_without_a_signer_pin_cannot_exist(string? signer)
    {
        Should.Throw<ArgumentException>(() => Create(signer: signer!));
    }

    // ---- content integrity -------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("0123456789abcdef")]
    [InlineData("zzzz456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void An_invalid_content_hash_is_refused(string sha)
    {
        Should.Throw<ArgumentException>(() => Create(sha: sha));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_size_is_refused(long size)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Create(size: size));
    }

    /// <summary>
    /// The ceiling is enforced in the domain as well as at the upload endpoint, so a
    /// package created by any other route is still bounded.
    /// </summary>
    [Fact]
    public void An_oversized_package_is_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => Create(size: DriverPackage.MaxArchiveBytes + 1));
    }

    // ---- names that must not be paths --------------------------------------

    /// <summary>
    /// Neither name is ever joined to a path -- the endpoint locates the INF among
    /// the extracted files -- but the cheapest place to make a path impossible is
    /// where the value is created.
    /// </summary>
    [Theory]
    [InlineData("../escape.inf")]
    [InlineData(@"..\escape.inf")]
    [InlineData("sub/contoso.inf")]
    [InlineData(@"C:\Windows\INF\usbstor.inf")]
    [InlineData("/etc/passwd.inf")]
    public void An_inf_name_carrying_a_path_is_refused(string infFileName)
    {
        Should.Throw<ArgumentException>(() => Create(infFileName: infFileName));
    }

    [Theory]
    [InlineData("../package.zip")]
    [InlineData(@"C:\temp\package.zip")]
    [InlineData("sub/package.zip")]
    public void A_file_name_carrying_a_path_is_refused(string fileName)
    {
        Should.Throw<ArgumentException>(() => Create(fileName: fileName));
    }

    /// <summary>
    /// The entry point must be an INF. Anything else would be a package this
    /// platform has no installation path for.
    /// </summary>
    [Theory]
    [InlineData("contoso.exe")]
    [InlineData("contoso.sys")]
    [InlineData("contoso")]
    [InlineData("contoso.inf.exe")]
    public void An_entry_point_that_is_not_an_inf_is_refused(string infFileName)
    {
        Should.Throw<ArgumentException>(() => Create(infFileName: infFileName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_package_without_a_hardware_id_is_refused(string hardwareId)
    {
        Should.Throw<ArgumentException>(() => Create(hardwareId: hardwareId));
    }

    // ---- withdrawal --------------------------------------------------------

    [Fact]
    public void Withdrawing_marks_the_package_and_records_when()
    {
        var package = Create();
        var now = DateTimeOffset.UtcNow;

        package.Withdraw(now).ShouldBeTrue();
        package.IsWithdrawn.ShouldBeTrue();
        package.WithdrawnAt.ShouldBe(now);
    }

    /// <summary>
    /// Idempotent, and the original timestamp survives: a second withdrawal is not a
    /// new event and must not overwrite when the decision was actually taken.
    /// </summary>
    [Fact]
    public void Withdrawing_twice_changes_nothing()
    {
        var package = Create();
        var first = DateTimeOffset.UtcNow;

        package.Withdraw(first);
        package.Withdraw(first.AddHours(1)).ShouldBeFalse();

        package.WithdrawnAt.ShouldBe(first);
    }
}
