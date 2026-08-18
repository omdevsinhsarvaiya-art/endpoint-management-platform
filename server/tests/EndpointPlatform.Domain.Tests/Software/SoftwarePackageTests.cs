using EndpointPlatform.Domain.Software;

namespace EndpointPlatform.Domain.Tests.Software;

public sealed class SoftwarePackageTests
{
    private const string ValidSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static SoftwarePackage Create(
        string sha = ValidSha, string fileName = "app.msi",
        string productCode = "{2C4E1D0B-1111-2222-3333-444455556666}", string? signer = "CN=Contoso") =>
        new(Guid.CreateVersion7(), "Contoso App", "1.2.3", "Contoso", SoftwarePackageType.WindowsInstaller,
            sha, fileName, 4096, productCode, signer, Guid.CreateVersion7(), "admin");

    [Fact]
    public void A_valid_package_normalizes_hash_and_product_code()
    {
        var pkg = Create(sha: ValidSha.ToUpperInvariant(), productCode: "2c4e1d0b-1111-2222-3333-444455556666");

        pkg.Sha256.ShouldBe(ValidSha); // lowercased
        pkg.MsiProductCode.ShouldBe("{2C4E1D0B-1111-2222-3333-444455556666}"); // braced, uppercase
        pkg.IsWithdrawn.ShouldBeFalse();
    }

    [Theory]
    [InlineData("short")]
    [InlineData("zzzz56789abcdef0123456789abcdef0123456789abcdef0123456789abcdef00")]
    public void A_bad_hash_is_rejected(string badHash) =>
        Should.Throw<ArgumentException>(() => Create(sha: badHash));

    [Theory]
    [InlineData("app.exe")]
    [InlineData("setup.bat")]
    [InlineData("..\\evil.msi")]
    [InlineData("sub/app.msi")]
    public void Only_a_plain_msi_file_name_is_accepted(string fileName) =>
        Should.Throw<ArgumentException>(() => Create(fileName: fileName));

    [Fact]
    public void A_non_guid_product_code_is_rejected() =>
        Should.Throw<ArgumentException>(() => Create(productCode: "not-a-guid"));

    [Fact]
    public void A_null_signer_is_allowed()
    {
        var pkg = Create(signer: null);
        pkg.RequiredSignerSubject.ShouldBeNull();
    }

    [Fact]
    public void Withdraw_is_idempotent_and_timestamps_once()
    {
        var pkg = Create();
        var first = DateTimeOffset.UtcNow;
        pkg.Withdraw(first);
        pkg.Withdraw(first.AddHours(1));

        pkg.IsWithdrawn.ShouldBeTrue();
        pkg.WithdrawnAt.ShouldBe(first);
    }
}
