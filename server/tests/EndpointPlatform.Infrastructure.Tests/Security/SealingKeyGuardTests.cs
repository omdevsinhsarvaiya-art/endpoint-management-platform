using System.Security.Cryptography;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.Extensions.Configuration;

namespace EndpointPlatform.Infrastructure.Tests.Security;

/// <summary>
/// The two startup guards that keep the escrow key boundary honest.
/// </summary>
/// <remarks>
/// Both exist because the failures they prevent are silent. One would let the
/// endpoint-facing process decrypt the estate's recovery passwords; the other would
/// let every escrow succeed while no one could ever read one back. Neither shows up
/// in a health check, and both are configuration mistakes rather than code ones,
/// which is why they are asserted at startup rather than reviewed.
/// </remarks>
public sealed class SealingKeyGuardTests
{
    private static readonly RSA Key = RSA.Create(3072);

    private static string PublicKey => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());

    private static string PrivateKey => Convert.ToBase64String(Key.ExportPkcs8PrivateKey());

    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s =>
                new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    // ---- the Agent API must hold nothing that decrypts ---------------------

    [Fact]
    public void The_agent_api_starts_with_only_a_public_key()
    {
        Should.NotThrow(() => AgentApiKeyBoundaryGuard.AssertNoDecryptionKeys(
            Config(("RecoveryEscrow:SealingPublicKey", PublicKey))));
    }

    [Theory]
    [InlineData("RecoveryEscrow:Key")]
    [InlineData("RecoveryEscrow:SealingPrivateKey")]
    public void The_agent_api_refuses_to_start_with_decryption_key_material(string setting)
    {
        var refusal = Should.Throw<InvalidOperationException>(
            () => AgentApiKeyBoundaryGuard.AssertNoDecryptionKeys(
                Config((setting, "any-value-at-all"), ("RecoveryEscrow:SealingPublicKey", PublicKey))));

        refusal.Message.ShouldContain(setting);

        // Names the setting, never what was in it.
        refusal.Message.ShouldNotContain("any-value-at-all");
    }

    // ---- the Admin API must be able to read back what endpoints seal --------

    /// <summary>
    /// The Phase 3 gap: a public key with no private half meant every escrow
    /// succeeded and none could ever be revealed, discovered only when a key was
    /// needed.
    /// </summary>
    [Fact]
    public void A_public_key_without_its_private_half_is_refused()
    {
        var refusal = Should.Throw<InvalidOperationException>(
            () => AdminApiSealingKeyGuard.AssertRevealRemainsPossible(
                Config(("RecoveryEscrow:SealingPublicKey", PublicKey))));

        refusal.Message.ShouldContain("SealingPrivateKey");
    }

    /// <summary>
    /// Two valid keys that are not the same pair fail exactly as badly as a missing
    /// one, and are far easier to configure by accident.
    /// </summary>
    [Fact]
    public void A_mismatched_keypair_is_refused()
    {
        using var other = RSA.Create(3072);

        var refusal = Should.Throw<InvalidOperationException>(
            () => AdminApiSealingKeyGuard.AssertRevealRemainsPossible(
                Config(
                    ("RecoveryEscrow:SealingPublicKey", PublicKey),
                    ("RecoveryEscrow:SealingPrivateKey",
                        Convert.ToBase64String(other.ExportPkcs8PrivateKey())))));

        refusal.Message.ShouldContain("not the same keypair");
    }

    [Fact]
    public void A_matching_keypair_is_accepted()
    {
        Should.NotThrow(() => AdminApiSealingKeyGuard.AssertRevealRemainsPossible(
            Config(
                ("RecoveryEscrow:SealingPublicKey", PublicKey),
                ("RecoveryEscrow:SealingPrivateKey", PrivateKey))));
    }

    /// <summary>
    /// Automatic escrow switched off entirely. An ordinary state, and not one worth
    /// refusing to boot over.
    /// </summary>
    [Fact]
    public void No_sealing_key_at_all_is_accepted()
    {
        Should.NotThrow(() => AdminApiSealingKeyGuard.AssertRevealRemainsPossible(Config()));
    }

    [Fact]
    public void A_malformed_private_key_is_refused()
    {
        var refusal = Should.Throw<InvalidOperationException>(
            () => AdminApiSealingKeyGuard.AssertRevealRemainsPossible(
                Config(
                    ("RecoveryEscrow:SealingPublicKey", PublicKey),
                    ("RecoveryEscrow:SealingPrivateKey", "not-base64-pkcs8"))));

        refusal.Message.ShouldContain("PKCS#8");
    }

    [Fact]
    public void A_private_key_weaker_than_the_minimum_is_refused()
    {
        using var weak = RSA.Create(2048);

        var refusal = Should.Throw<InvalidOperationException>(
            () => AdminApiSealingKeyGuard.AssertRevealRemainsPossible(
                Config(
                    ("RecoveryEscrow:SealingPublicKey",
                        Convert.ToBase64String(weak.ExportSubjectPublicKeyInfo())),
                    ("RecoveryEscrow:SealingPrivateKey",
                        Convert.ToBase64String(weak.ExportPkcs8PrivateKey())))));

        refusal.Message.ShouldContain("3072");
    }
}
