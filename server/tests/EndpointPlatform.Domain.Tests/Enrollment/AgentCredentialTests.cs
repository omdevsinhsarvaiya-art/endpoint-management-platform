using EndpointPlatform.Domain.Enrollment;

namespace EndpointPlatform.Domain.Tests.Enrollment;

public sealed class AgentCredentialTests
{
    private static readonly Guid DeviceId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private static readonly string ValidKeyId = new('b', 32);
    private static readonly string ValidHash = new('c', 64);

    [Fact]
    public void A_new_credential_is_active()
    {
        var credential = new AgentCredential(DeviceId, ValidKeyId, ValidHash, Now);

        credential.IsActive.ShouldBeTrue();
        credential.DeviceId.ShouldBe(DeviceId);
        credential.LastUsedAt.ShouldBeNull();
    }

    [Fact]
    public void Revocation_deactivates_and_is_idempotent()
    {
        var credential = new AgentCredential(DeviceId, ValidKeyId, ValidHash, Now);

        credential.Revoke(Now);
        credential.Revoke(Now.AddHours(1));

        credential.IsActive.ShouldBeFalse();
        credential.RevokedAt.ShouldBe(Now);
    }

    [Fact]
    public void Recording_use_updates_the_timestamp()
    {
        var credential = new AgentCredential(DeviceId, ValidKeyId, ValidHash, Now);

        credential.RecordUse(Now.AddMinutes(5));

        credential.LastUsedAt.ShouldBe(Now.AddMinutes(5));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")] // uppercase
    public void Malformed_key_ids_are_rejected(string keyId)
    {
        Should.Throw<ArgumentException>(() => new AgentCredential(DeviceId, keyId, ValidHash, Now));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("not-hex-not-hex-not-hex-not-hex-not-hex-not-hex-not-hex-not-hex-")]
    public void Malformed_secret_hashes_are_rejected(string hash)
    {
        Should.Throw<ArgumentException>(() => new AgentCredential(DeviceId, ValidKeyId, hash, Now));
    }

    [Fact]
    public void The_entity_never_holds_a_plaintext_secret()
    {
        typeof(AgentCredential).GetProperties()
            .Select(p => p.Name)
            .ShouldNotContain("Secret", "only the hash may be persisted");
    }
}
