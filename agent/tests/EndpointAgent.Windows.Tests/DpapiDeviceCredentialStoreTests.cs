using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Configuration;
using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// Windows integration tests against real DPAPI, using a per-test temp state
/// directory. These run unelevated: the ACL-hardening branch logs a warning in
/// that case, and the DPAPI protection itself needs no elevation.
/// </summary>
public sealed class DpapiDeviceCredentialStoreTests : IDisposable
{
    private readonly string _stateDirectory;
    private readonly DpapiDeviceCredentialStore _store;

    public DpapiDeviceCredentialStoreTests()
    {
        _stateDirectory = Path.Combine(Path.GetTempPath(), $"epa-test-{Guid.CreateVersion7():N}");

        _store = new DpapiDeviceCredentialStore(
            Options.Create(new AgentOptions
            {
                ServerBaseUrl = "https://localhost:5081",
                StateDirectory = _stateDirectory,
            }),
            NullLogger<DpapiDeviceCredentialStore>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory))
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
    }

    private static DeviceCredential MakeCredential() =>
        new(Guid.CreateVersion7(), new string('a', 32), new string('b', 64));

    /// <summary>
    /// The sealing-key fingerprint must survive the DPAPI round trip.
    /// </summary>
    /// <remarks>
    /// It is the value that decides whether this machine may collect a recovery
    /// password at all, and it is stored alongside the credential rather than
    /// anywhere else precisely so that revoking one revokes the other. A round trip
    /// that dropped it would leave a device permanently and silently ineligible.
    /// </remarks>
    [Fact]
    public async Task A_pinned_fingerprint_survives_the_round_trip()
    {
        const string fingerprint = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

        var credential = new DeviceCredential(
            Guid.CreateVersion7(), new string('a', 32), new string('b', 64), fingerprint);

        await _store.SaveAsync(credential);
        var loaded = await _store.LoadAsync();

        loaded.ShouldNotBeNull();
        loaded.SealingKeyFingerprint.ShouldBe(fingerprint);
        loaded.IsAutomaticEscrowEligible.ShouldBeTrue();
    }

    /// <summary>
    /// A blob written before automatic escrow existed has no fingerprint field.
    /// It must still load -- the device keeps working -- and must be ineligible.
    /// </summary>
    /// <remarks>
    /// This is the upgrade path for every device already in the field. The field is
    /// nullable and last in the persisted record so an older blob deserializes with
    /// it absent, which is exactly the ineligible state required.
    /// </remarks>
    [Fact]
    public async Task A_credential_stored_without_a_fingerprint_loads_and_is_ineligible()
    {
        var credential = new DeviceCredential(
            Guid.CreateVersion7(), new string('a', 32), new string('b', 64));

        await _store.SaveAsync(credential);
        var loaded = await _store.LoadAsync();

        loaded.ShouldNotBeNull();
        loaded.KeyId.ShouldBe(credential.KeyId);
        loaded.SealingKeyFingerprint.ShouldBeNull();
        loaded.IsAutomaticEscrowEligible.ShouldBeFalse();
    }

    /// <summary>Re-enrollment overwrites the stored pin rather than keeping the old one.</summary>
    [Fact]
    public async Task Saving_again_replaces_the_stored_fingerprint()
    {
        var deviceId = Guid.CreateVersion7();

        await _store.SaveAsync(new DeviceCredential(
            deviceId, new string('a', 32), new string('b', 64), new string('1', 64)));

        await _store.SaveAsync(new DeviceCredential(
            deviceId, new string('c', 32), new string('d', 64), new string('2', 64)));

        var loaded = await _store.LoadAsync();

        loaded!.SealingKeyFingerprint.ShouldBe(new string('2', 64));
    }

    /// <summary>
    /// The credential must never render its secret, fingerprint included in the
    /// check because a future edit could add it to the same string.
    /// </summary>
    [Fact]
    public void The_credential_never_renders_its_secret()
    {
        var rendered = new DeviceCredential(
            Guid.CreateVersion7(), new string('a', 32), "super-secret-value", new string('f', 64))
            .ToString();

        rendered.ShouldNotContain("super-secret-value");
        rendered.ShouldContain("redacted");
    }

    [Fact]
    public async Task Load_returns_null_when_nothing_was_stored()
    {
        (await _store.LoadAsync()).ShouldBeNull();
        (await _store.HasCredentialAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task A_saved_credential_round_trips()
    {
        var credential = MakeCredential();

        await _store.SaveAsync(credential);
        var loaded = await _store.LoadAsync();

        loaded.ShouldBe(credential);
    }

    [Fact]
    public async Task The_credential_is_not_stored_in_plaintext()
    {
        var credential = MakeCredential();
        await _store.SaveAsync(credential);

        var file = Path.Combine(_stateDirectory, "device-credential.bin");
        File.Exists(file).ShouldBeTrue();

        var raw = await File.ReadAllBytesAsync(file);
        var rawText = System.Text.Encoding.UTF8.GetString(raw);

        rawText.ShouldNotContain(credential.Secret, customMessage: "the secret must never touch disk unencrypted");
        rawText.ShouldNotContain(credential.KeyId);
        rawText.ShouldNotContain(credential.DeviceId.ToString());
    }

    [Fact]
    public async Task Clear_removes_the_credential()
    {
        await _store.SaveAsync(MakeCredential());

        await _store.ClearAsync();

        (await _store.LoadAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task Saving_twice_keeps_only_the_newest_credential()
    {
        var first = MakeCredential();
        var second = MakeCredential();

        await _store.SaveAsync(first);
        await _store.SaveAsync(second);

        (await _store.LoadAsync()).ShouldBe(second);
    }

    [Fact]
    public async Task A_corrupt_blob_is_treated_as_not_enrolled_rather_than_crashing()
    {
        await _store.SaveAsync(MakeCredential());

        var file = Path.Combine(_stateDirectory, "device-credential.bin");
        var bytes = await File.ReadAllBytesAsync(file);
        // Flip bits in the middle of the protected blob.
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(file, bytes);

        var loaded = await _store.LoadAsync();

        loaded.ShouldBeNull("a corrupt credential must force re-enrollment, not crash the service");
    }
}
