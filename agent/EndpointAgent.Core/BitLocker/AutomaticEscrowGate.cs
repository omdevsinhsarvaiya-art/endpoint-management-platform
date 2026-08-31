using System.Security.Cryptography;
using EndpointAgent.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.BitLocker;

/// <summary>Why automatic escrow did not produce an envelope.</summary>
/// <remarks>
/// A closed set of categories, never a message. The failure surface of a component
/// that handles a disk-unlock credential is the likeliest place for one to escape
/// into an append-only audit trail, so there is deliberately nowhere here to put a
/// value.
/// </remarks>
public enum AutomaticEscrowOutcome
{
    /// <summary>Sealed and ready to upload.</summary>
    Sealed = 0,

    /// <summary>No pinned fingerprint. The device must re-enroll first.</summary>
    NotEligible = 1,

    /// <summary>The offered key did not match the pin.</summary>
    FingerprintMismatch = 2,

    /// <summary>This protector is already escrowed; nothing to do.</summary>
    AlreadyEscrowed = 3,

    /// <summary>Windows refused to return the password.</summary>
    WindowsRefused = 4,

    /// <summary>The protector disappeared between detection and retrieval.</summary>
    ProtectorGone = 5,

    /// <summary>Windows returned something that is not a valid recovery password.</summary>
    MalformedPassword = 6,

    /// <summary>Sealing itself failed.</summary>
    SealingFailed = 7,

    /// <summary>The protector is not one this volume reported.</summary>
    NotAssociated = 8,

    /// <summary>An attempt is scheduled but not yet owed.</summary>
    RetryNotDue = 9,

    /// <summary>Every scheduled attempt failed; only an administrator re-arms it.</summary>
    RetryExhausted = 10,

    /// <summary>The stored credential is not usable.</summary>
    CredentialInactive = 11,
}

/// <summary>The server's verdict on whether an attempt is owed.</summary>
/// <remarks>
/// Supplied by the server rather than computed here. An agent that decided its own
/// backoff would reset it on every restart, and a restart loop would then hammer
/// both Windows and the API.
/// </remarks>
public enum AutomaticEscrowRetry
{
    Due = 0,
    NotDue = 1,
    Exhausted = 2,
}

/// <summary>The result of one gated attempt. Carries an envelope, never a password.</summary>
public sealed record AutomaticEscrowResult(
    AutomaticEscrowOutcome Outcome,
    RecoveryEscrowEnvelope? Envelope,
    string? ProtectorId)
{
    public bool Succeeded => Outcome == AutomaticEscrowOutcome.Sealed && Envelope is not null;

    public static AutomaticEscrowResult Blocked(AutomaticEscrowOutcome outcome, string? protectorId = null) =>
        new(outcome, null, protectorId);
}

/// <summary>
/// Decides whether a recovery password may be read, and seals it if so.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ordering of the checks is the security property, not an optimisation.</b>
/// Eligibility, then the pinned fingerprint, then whether the protector is already
/// escrowed -- all before <see cref="IRecoveryPasswordReader"/> is touched. Each
/// gate that fails means the credential is never read from Windows at all, rather
/// than read and then discarded. A discarded credential has still existed in this
/// process's memory; one that was never retrieved has not.
/// </para>
/// <para>
/// This type performs no I/O of its own beyond the reader it is given. Uploading
/// belongs to the transport layer, deliberately separate: this decides and seals,
/// and what it returns is safe to hand anywhere.
/// </para>
/// </remarks>
public sealed class AutomaticEscrowGate(
    IRecoveryPasswordReader reader,
    ILogger<AutomaticEscrowGate> logger)
{
    private readonly IRecoveryPasswordReader _reader = reader
        ?? throw new ArgumentNullException(nameof(reader));

    private readonly ILogger<AutomaticEscrowGate> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Runs every gate and, if all pass, reads and seals the password.
    /// </summary>
    /// <param name="credential">
    /// The device credential. A null <c>SealingKeyFingerprint</c> makes the device
    /// ineligible and stops this immediately.
    /// </param>
    /// <param name="sealingKey">The public key offered by the server for this attempt.</param>
    /// <param name="alreadyEscrowed">
    /// Whether the server already holds an escrow for this exact protector. Passed
    /// in rather than queried here so that the check is explicit at the call site
    /// and testable without a transport.
    /// </param>
    public async Task<AutomaticEscrowResult> TrySealAsync(
        DeviceCredential credential,
        RSA? sealingKey,
        string volumeDeviceIdentifier,
        IReadOnlyCollection<string> volumeProtectorIds,
        string keyProtectorId,
        bool alreadyEscrowed,
        AutomaticEscrowRetry retry = AutomaticEscrowRetry.Due,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(volumeProtectorIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeDeviceIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyProtectorId);

        // ---- Gate 0: the credential must be usable -------------------------
        if (string.IsNullOrWhiteSpace(credential.KeyId) || string.IsNullOrWhiteSpace(credential.Secret))
        {
            return AutomaticEscrowResult.Blocked(AutomaticEscrowOutcome.CredentialInactive, keyProtectorId);
        }

        // ---- Gate 1: eligibility ------------------------------------------
        // No pin, no escrow. A device enrolled before automatic escrow existed
        // reaches here with a null fingerprint and stops, keeping full BitLocker
        // inventory while collecting nothing.
        if (!credential.IsAutomaticEscrowEligible)
        {
            _logger.LogDebug(
                "Automatic escrow skipped for protector {Protector}: this device has no pinned "
                + "sealing key and must re-enroll.", keyProtectorId);

            return AutomaticEscrowResult.Blocked(AutomaticEscrowOutcome.NotEligible, keyProtectorId);
        }

        // ---- Gate 2: the pin ----------------------------------------------
        // Verified here as well as inside the sealer. The sealer's check is what
        // makes sealing safe; this one is what keeps the password from being read
        // in the first place when the key is wrong.
        if (sealingKey is null)
        {
            return AutomaticEscrowResult.Blocked(AutomaticEscrowOutcome.FingerprintMismatch, keyProtectorId);
        }

        string offered;
        try
        {
            offered = RecoveryPasswordSealer.Fingerprint(sealingKey);
        }
        catch (CryptographicException)
        {
            return AutomaticEscrowResult.Blocked(AutomaticEscrowOutcome.FingerprintMismatch, keyProtectorId);
        }

        if (!string.Equals(offered, credential.SealingKeyFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            // Worth a warning rather than a debug line: the benign explanation is a
            // server-side key rotation, and the other explanation is someone
            // standing in front of the server.
            _logger.LogWarning(
                "Automatic escrow refused for protector {Protector}: the offered sealing key does "
                + "not match the fingerprint pinned at enrollment. No recovery password was read.",
                keyProtectorId);

            return AutomaticEscrowResult.Blocked(AutomaticEscrowOutcome.FingerprintMismatch, keyProtectorId);
        }

        // ---- Gate 3: association ------------------------------------------
        // The protector must belong to the volume it is being escrowed against.
        // The server refuses the upload otherwise, but checking here means a
        // mismatch never causes a password to be read in the first place.
        if (!volumeProtectorIds.Any(reported => ProtectorIdsMatch(reported, keyProtectorId)))
        {
            _logger.LogWarning(
                "Automatic escrow skipped: protector {Protector} is not one volume {Volume} reported.",
                keyProtectorId, volumeDeviceIdentifier);

            return AutomaticEscrowResult.Blocked(AutomaticEscrowOutcome.NotAssociated, keyProtectorId);
        }

        // ---- Gate 4: deduplication ----------------------------------------
        // Idempotence, and a privacy property: a machine whose key is already
        // filed never materialises that key again on any subsequent inventory.
        if (alreadyEscrowed)
        {
            return AutomaticEscrowResult.Blocked(AutomaticEscrowOutcome.AlreadyEscrowed, keyProtectorId);
        }

        // ---- Gate 5: the retry schedule ------------------------------------
        // Last gate before retrieval, and the one that stops a machine whose
        // Windows keeps refusing from being asked again every few seconds.
        if (retry != AutomaticEscrowRetry.Due)
        {
            return AutomaticEscrowResult.Blocked(
                retry == AutomaticEscrowRetry.Exhausted
                    ? AutomaticEscrowOutcome.RetryExhausted
                    : AutomaticEscrowOutcome.RetryNotDue,
                keyProtectorId);
        }

        // ---- All gates passed: the password may now be read ----------------
        var read = await _reader.ReadAsync(volumeDeviceIdentifier, keyProtectorId, cancellationToken);

        if (!read.Success)
        {
            return AutomaticEscrowResult.Blocked(
                read.Status switch
                {
                    RecoveryPasswordReadStatus.ProtectorGone => AutomaticEscrowOutcome.ProtectorGone,
                    RecoveryPasswordReadStatus.Malformed => AutomaticEscrowOutcome.MalformedPassword,
                    _ => AutomaticEscrowOutcome.WindowsRefused,
                },
                keyProtectorId);
        }

        // The reader reports which protector it actually read. Windows is asked for
        // one specific protector, but a mismatch here would mean a key filed
        // against the wrong protector -- discovered only when it failed to unlock
        // something -- so it is checked rather than assumed.
        if (read.ProtectorId is not null
            && !ProtectorIdsMatch(read.ProtectorId, keyProtectorId))
        {
            _logger.LogWarning(
                "Automatic escrow refused: the recovery password returned belongs to protector "
                + "{Returned}, not the requested {Requested}.", read.ProtectorId, keyProtectorId);

            return AutomaticEscrowResult.Blocked(AutomaticEscrowOutcome.MalformedPassword, keyProtectorId);
        }

        if (!RecoveryPasswordFormat.IsWellFormed(read.RecoveryPassword))
        {
            // The server cannot open the envelope, so this is the only place the
            // value is ever checked. Never says what was wrong with it beyond the
            // category.
            _logger.LogWarning(
                "Automatic escrow refused for protector {Protector}: Windows returned a value that "
                + "is not a well-formed recovery password.", keyProtectorId);

            return AutomaticEscrowResult.Blocked(AutomaticEscrowOutcome.MalformedPassword, keyProtectorId);
        }

        try
        {
            var envelope = RecoveryPasswordSealer.Seal(
                read.RecoveryPassword!, sealingKey, credential.SealingKeyFingerprint!);

            return new AutomaticEscrowResult(AutomaticEscrowOutcome.Sealed, envelope, keyProtectorId);
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException)
        {
            // Deliberately does not pass the exception to the logger. Sealing
            // failures are cryptographic-state problems, and the category is all a
            // reader needs; the exception is not worth the risk of what a future
            // implementation might put in its message.
            _logger.LogWarning(
                "Automatic escrow could not seal the recovery password for protector {Protector}.",
                keyProtectorId);

            return AutomaticEscrowResult.Blocked(AutomaticEscrowOutcome.SealingFailed, keyProtectorId);
        }
    }

    /// <summary>
    /// Compares protector GUIDs regardless of brace style or casing, matching how
    /// the server normalises them.
    /// </summary>
    private static bool ProtectorIdsMatch(string left, string right) =>
        Guid.TryParse(left.Trim().Trim('{', '}'), out var a)
        && Guid.TryParse(right.Trim().Trim('{', '}'), out var b)
        && a == b;
}
