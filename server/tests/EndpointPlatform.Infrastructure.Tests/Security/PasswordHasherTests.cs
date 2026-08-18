using EndpointPlatform.Infrastructure.Security;

namespace EndpointPlatform.Infrastructure.Tests.Security;

public sealed class PasswordHasherTests
{
    [Fact]
    public void A_hashed_password_verifies()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");

        PasswordHasher.Verify("correct horse battery staple", hash).ShouldBeTrue();
    }

    [Fact]
    public void The_wrong_password_does_not_verify()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");

        PasswordHasher.Verify("wrong horse", hash).ShouldBeFalse();
    }

    [Fact]
    public void Hashing_the_same_password_twice_produces_different_hashes()
    {
        // Per-hash random salt: equal passwords must not be linkable at rest.
        PasswordHasher.Hash("same password").ShouldNotBe(PasswordHasher.Hash("same password"));
    }

    [Fact]
    public void The_encoding_carries_scheme_and_iterations()
    {
        var hash = PasswordHasher.Hash("x-marks-the-spot");

        hash.ShouldStartWith("pbkdf2-sha256$600000$");
        hash.Split('$').Length.ShouldBe(4);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("md5$1$abc$def")]
    [InlineData("pbkdf2-sha256$notanumber$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$600000$!!notbase64!!$aGFzaA==")]
    [InlineData("pbkdf2-sha256$-5$c2FsdA==$aGFzaA==")]
    public void Malformed_or_foreign_encodings_verify_as_false_not_as_exceptions(string encoded)
    {
        PasswordHasher.Verify("anything", encoded).ShouldBeFalse();
    }

    [Fact]
    public void A_current_hash_does_not_need_rehash()
    {
        PasswordHasher.NeedsRehash(PasswordHasher.Hash("pw")).ShouldBeFalse();
    }

    [Fact]
    public void A_weaker_iteration_count_needs_rehash()
    {
        // Simulate a legacy record hashed under an older, lower policy.
        var current = PasswordHasher.Hash("pw");
        var weakened = current.Replace("$600000$", "$100000$");

        PasswordHasher.NeedsRehash(weakened).ShouldBeTrue();
    }

    [Fact]
    public void Unicode_passwords_round_trip()
    {
        var hash = PasswordHasher.Hash("pässwörd-日本語-🔑");

        PasswordHasher.Verify("pässwörd-日本語-🔑", hash).ShouldBeTrue();
        PasswordHasher.Verify("pässwörd-日本語-x", hash).ShouldBeFalse();
    }
}
