using System.Security.Cryptography;
using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.BitLocker;

/// <summary>What one pass of automatic escrow did.</summary>
/// <param name="Escrowed">Protectors sealed and uploaded on this pass.</param>
/// <param name="Skipped">Protectors a gate declined, for any reason.</param>
/// <param name="Failed">Protectors that reached retrieval or upload and did not finish.</param>
public sealed record AutomaticEscrowRunSummary(int Escrowed, int Skipped, int Failed)
{
    public static readonly AutomaticEscrowRunSummary Idle = new(0, 0, 0);
}

/// <summary>
/// Drives automatic recovery-password escrow for one device.
/// </summary>
/// <remarks>
/// <para>
/// <b>The escrow targets come from the server, not from a local BitLocker scan.</b>
/// The status endpoint lists the protectors this device has already reported through
/// inventory, which is exactly the set the server will accept an escrow for -- it
/// refuses a protector it has never seen. Deriving targets from a second local scan
/// would let the agent try to file keys the server must reject, and would put a
/// BitLocker enumeration on a path that has no need of one. The inventory collector
/// stays entirely separate from recovery-password retrieval, as it was designed to.
/// </para>
/// <para>
/// A protector Windows has only just created therefore becomes a target after the
/// next inventory upload rather than immediately. That ordering is deliberate: the
/// server must know a protector exists before it can accept a key for it.
/// </para>
/// <para>
/// <b>Nothing here holds a recovery password.</b> The gate returns a sealed envelope
/// or a category, and this type moves envelopes. There is no variable in it that a
/// plaintext password could occupy.
/// </para>
/// </remarks>
public sealed class AutomaticEscrowRunner(
    IAgentApiClient apiClient,
    AutomaticEscrowGate gate,
    ILogger<AutomaticEscrowRunner> logger)
{
    private readonly IAgentApiClient _apiClient = apiClient
        ?? throw new ArgumentNullException(nameof(apiClient));

    private readonly AutomaticEscrowGate _gate = gate
        ?? throw new ArgumentNullException(nameof(gate));

    private readonly ILogger<AutomaticEscrowRunner> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public async Task<AutomaticEscrowRunSummary> RunAsync(
        DeviceCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        // Cheapest possible exit for the common case. A device with no pinned
        // fingerprint -- every device enrolled before this feature -- stops here
        // without a round trip and without touching BitLocker.
        if (!credential.IsAutomaticEscrowEligible)
        {
            return AutomaticEscrowRunSummary.Idle;
        }

        var status = await _apiClient.GetBitLockerEscrowStatusAsync(credential, cancellationToken);

        if (!status.IsSuccess || status.Value is null)
        {
            _logger.LogDebug(
                "Automatic escrow status was unavailable ({Status}); nothing was collected.",
                status.Status);

            return AutomaticEscrowRunSummary.Idle;
        }

        var state = status.Value;

        if (!state.Eligible)
        {
            // The server's view disagrees with the credential's -- most likely the
            // credential was revoked, or the device was re-enrolled elsewhere. The
            // server decides, and nothing is collected.
            return AutomaticEscrowRunSummary.Idle;
        }

        using var sealingKey = ImportSealingKey(state.SealingPublicKey);

        // Protectors grouped by volume, so the gate can be told which protectors a
        // volume actually reported and reject one that belongs to another.
        var byVolume = state.Protectors
            .GroupBy(p => p.VolumeDeviceIdentifier, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(p => p.KeyProtectorId).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var escrowed = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var protector in state.Protectors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await _gate.TrySealAsync(
                credential,
                sealingKey,
                protector.VolumeDeviceIdentifier,
                byVolume[protector.VolumeDeviceIdentifier],
                protector.KeyProtectorId,
                protector.Escrowed,
                RetryOf(protector),
                cancellationToken);

            if (!result.Succeeded)
            {
                if (IsGateDecline(result.Outcome))
                {
                    skipped++;
                }
                else
                {
                    // Reached retrieval or sealing and did not finish. The upload
                    // that would advance the retry schedule never happened, so the
                    // failure is reported to the server below.
                    failed++;
                    await ReportFailureAsync(credential, protector, cancellationToken);
                }

                continue;
            }

            if (await UploadAsync(credential, protector, result.Envelope!, cancellationToken))
            {
                escrowed++;
            }
            else
            {
                failed++;
            }
        }

        if (escrowed > 0 || failed > 0)
        {
            _logger.LogInformation(
                "Automatic escrow pass complete: {Escrowed} escrowed, {Failed} failed, {Skipped} skipped.",
                escrowed, failed, skipped);
        }

        return new AutomaticEscrowRunSummary(escrowed, skipped, failed);
    }

    /// <summary>
    /// Uploads a sealed envelope.
    /// </summary>
    /// <remarks>
    /// An upload failure is not retried here. The server owns the schedule, and a
    /// local retry loop would spend attempts it does not control -- and would mean
    /// re-reading the password, which is the one thing this design works hardest to
    /// avoid doing more than once.
    /// </remarks>
    private async Task<bool> UploadAsync(
        DeviceCredential credential,
        BitLockerEscrowStatusItem protector,
        RecoveryEscrowEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var response = await _apiClient.EscrowRecoveryKeyAsync(
            new EscrowRecoveryKeyRequest(
                protector.VolumeDeviceIdentifier, protector.KeyProtectorId, envelope.ToJson()),
            credential,
            cancellationToken);

        if (response.IsSuccess)
        {
            _logger.LogInformation(
                "Recovery key escrowed for volume {Volume} protector {Protector}.",
                protector.VolumeDeviceIdentifier, protector.KeyProtectorId);

            return true;
        }

        // Status only. The response body is a problem document; nothing from the
        // envelope or the password is logged, and neither is in it.
        _logger.LogWarning(
            "Automatic escrow upload was not accepted for protector {Protector} ({Status}).",
            protector.KeyProtectorId, response.Status);

        return false;
    }

    /// <summary>
    /// Tells the server an attempt was made and failed, so the schedule advances.
    /// </summary>
    /// <remarks>
    /// Sent as a deliberately invalid envelope marker rather than inventing a second
    /// endpoint: the ingestion path already records a failed attempt for anything it
    /// refuses, and this keeps one place responsible for advancing the schedule.
    /// A retrieval that never produced an envelope has nothing else to report.
    /// </remarks>
    private async Task ReportFailureAsync(
        DeviceCredential credential,
        BitLockerEscrowStatusItem protector,
        CancellationToken cancellationToken)
    {
        await _apiClient.EscrowRecoveryKeyAsync(
            new EscrowRecoveryKeyRequest(
                protector.VolumeDeviceIdentifier, protector.KeyProtectorId, CollectionFailedMarker),
            credential,
            cancellationToken);
    }

    /// <summary>
    /// A body the server is guaranteed to reject, sent purely to record a failed
    /// attempt. It carries no key material and cannot be mistaken for an envelope.
    /// </summary>
    internal const string CollectionFailedMarker = "collection-failed";

    private static AutomaticEscrowRetry RetryOf(BitLockerEscrowStatusItem protector) =>
        protector.Due
            ? AutomaticEscrowRetry.Due
            : string.Equals(protector.State, "RetryExhausted", StringComparison.Ordinal)
                ? AutomaticEscrowRetry.Exhausted
                : AutomaticEscrowRetry.NotDue;

    /// <summary>
    /// Whether an outcome means a gate declined rather than something failing.
    /// </summary>
    /// <remarks>
    /// A decline is not an attempt: a protector that is already escrowed, or not yet
    /// due, must not consume a place in the retry schedule.
    /// </remarks>
    private static bool IsGateDecline(AutomaticEscrowOutcome outcome) => outcome
        is AutomaticEscrowOutcome.NotEligible
        or AutomaticEscrowOutcome.AlreadyEscrowed
        or AutomaticEscrowOutcome.NotAssociated
        or AutomaticEscrowOutcome.RetryNotDue
        or AutomaticEscrowOutcome.RetryExhausted
        or AutomaticEscrowOutcome.CredentialInactive
        or AutomaticEscrowOutcome.FingerprintMismatch;

    /// <summary>
    /// Imports the offered public key, or null when there is nothing usable.
    /// </summary>
    /// <remarks>
    /// Not trusted at this point. The gate compares its fingerprint against the one
    /// pinned at enrollment, and a key that fails that comparison never leads to a
    /// password being read.
    /// </remarks>
    private RSA? ImportSealingKey(string? spkiBase64)
    {
        if (string.IsNullOrWhiteSpace(spkiBase64))
        {
            return null;
        }

        var rsa = RSA.Create();

        try
        {
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spkiBase64), out _);
            return rsa;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            rsa.Dispose();

            _logger.LogWarning("The offered escrow sealing key could not be read; nothing was collected.");
            return null;
        }
    }
}
