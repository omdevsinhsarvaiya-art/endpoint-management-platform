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
