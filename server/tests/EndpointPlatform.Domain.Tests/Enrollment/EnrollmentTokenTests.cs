using EndpointPlatform.Domain.Enrollment;

namespace EndpointPlatform.Domain.Tests.Enrollment;

public sealed class EnrollmentTokenTests
{
    private static readonly Guid OrganizationId = Guid.CreateVersion7();
    private static readonly Guid AdminId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private static readonly string ValidHash = new('a', 64);

    private static EnrollmentToken CreateToken(
        DateTimeOffset? expiresAt = null,
        int maxUses = 5) =>
        new(
            OrganizationId,
            "Test batch",
            ValidHash,
            AdminId,
            "admin@company.local",
            expiresAt ?? Now.AddHours(24),
            maxUses);

    [Fact]
    public void A_new_token_is_usable()
    {
        var token = CreateToken();

        token.IsUsable(Now).ShouldBeTrue();
        token.UseCount.ShouldBe(0);
        token.IsRevoked.ShouldBeFalse();
    }

    [Fact]
    public void Consume_succeeds_and_increments_the_counter()
    {
        var token = CreateToken();

        token.TryConsume(Now).ShouldBe(EnrollmentTokenConsumeResult.Consumed);

        token.UseCount.ShouldBe(1);
    }

    [Fact]
    public void An_expired_token_is_refused()
    {
        var token = CreateToken(expiresAt: Now.AddHours(1));

        token.TryConsume(Now.AddHours(2)).ShouldBe(EnrollmentTokenConsumeResult.Expired);

        token.UseCount.ShouldBe(0, "a refused consume must not count as a use");
    }

    [Fact]
    public void Expiry_boundary_is_exclusive_of_the_expiry_instant()
    {
        var expiresAt = Now.AddHours(1);
        var token = CreateToken(expiresAt: expiresAt);

        token.IsExpired(expiresAt.AddTicks(-1)).ShouldBeFalse();
        token.IsExpired(expiresAt).ShouldBeTrue("a token is unusable AT its expiry instant");
    }

    [Fact]
    public void A_revoked_token_is_refused()
    {
        var token = CreateToken();
        token.Revoke(Now);

        token.TryConsume(Now.AddMinutes(1)).ShouldBe(EnrollmentTokenConsumeResult.Revoked);
    }

    [Fact]
    public void Revocation_wins_over_expiry_in_the_refusal_reason()
    {
        // An operator revoking a token wants the audit trail to say "revoked",
        // not "expired", regardless of which happened first.
        var token = CreateToken(expiresAt: Now.AddHours(1));
        token.Revoke(Now);

        token.TryConsume(Now.AddHours(2)).ShouldBe(EnrollmentTokenConsumeResult.Revoked);
    }

    [Fact]
    public void Revoking_twice_keeps_the_first_timestamp()
    {
        var token = CreateToken();

        token.Revoke(Now);
        token.Revoke(Now.AddHours(1));

        token.RevokedAt.ShouldBe(Now);
    }

    [Fact]
    public void Maximum_use_enforcement_is_exact()
    {
        var token = CreateToken(maxUses: 2);

        token.TryConsume(Now).ShouldBe(EnrollmentTokenConsumeResult.Consumed);
        token.TryConsume(Now).ShouldBe(EnrollmentTokenConsumeResult.Consumed);
        token.TryConsume(Now).ShouldBe(EnrollmentTokenConsumeResult.Exhausted);

        token.UseCount.ShouldBe(2, "the refused attempt must not increment the counter");
    }

    [Fact]
    public void A_single_use_token_works_exactly_once()
    {
        var token = CreateToken(maxUses: 1);

        token.TryConsume(Now).ShouldBe(EnrollmentTokenConsumeResult.Consumed);
        token.TryConsume(Now).ShouldBe(EnrollmentTokenConsumeResult.Exhausted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void Max_uses_outside_the_supported_range_is_rejected(int maxUses)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => CreateToken(maxUses: maxUses));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")] // uppercase
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")] // not hex
    public void Malformed_secret_hashes_are_rejected(string hash)
    {
        // The domain refuses anything that is not a lowercase hex SHA-256, so a
        // plaintext secret accidentally passed as the "hash" cannot be persisted.
        Should.Throw<ArgumentException>(() => new EnrollmentToken(
            OrganizationId, "name", hash, AdminId, "admin@company.local", Now.AddDays(1), 1));
    }

    [Fact]
    public void The_stored_hash_never_contains_the_secret()
    {
        // Structural guarantee: the constructor only accepts a 64-char hex string,
        // and the entity has no other secret-bearing member to leak.
        var token = CreateToken();

        token.SecretHash.ShouldBe(ValidHash);
        typeof(EnrollmentToken).GetProperties()
            .Select(p => p.Name)
            .ShouldNotContain("Secret", "the entity must not have a plaintext secret property");
    }
}
