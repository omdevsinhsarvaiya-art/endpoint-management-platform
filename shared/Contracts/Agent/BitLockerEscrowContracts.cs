namespace EndpointPlatform.Contracts.Agent;

/// <summary>
/// Request body for <c>POST /agent/v1/bitlocker/escrow</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is deliberately no field here that could carry a recovery password.</b>
/// The contract accepts a sealed envelope and identifiers, and nothing else. A
/// caller that tried to send plaintext would have nowhere to put it, and the
/// envelope validator rejects a bare password because it is not JSON.
/// </para>
/// <para>
/// The device is not named in this request. It is resolved from the authenticated
/// credential, so an agent cannot escrow against a machine that is not itself.
/// </para>
/// </remarks>
/// <param name="VolumeDeviceIdentifier">The volume, as reported by inventory.</param>
/// <param name="KeyProtectorId">The recovery protector this envelope unlocks.</param>
/// <param name="SealedEnvelope">
/// The serialised hybrid envelope produced on the endpoint. Opaque to the Agent
/// API, which holds no key able to open it.
/// </param>
public sealed record EscrowRecoveryKeyRequest(
    string VolumeDeviceIdentifier,
    string KeyProtectorId,
    string SealedEnvelope);

/// <summary>Outcome of an automatic escrow upload.</summary>
/// <param name="Status">
/// <c>escrowed</c> when stored, <c>already-escrowed</c> when this protector was
/// already filed. Both are successes: the second is what makes repeated inventory
/// idempotent.
/// </param>
/// <param name="EscrowId">Identifies the record, so the agent can correlate. Not a secret.</param>
public sealed record EscrowRecoveryKeyResponse(string Status, Guid? EscrowId);

/// <summary>
/// One protector's escrow state, as reported to the agent.
/// </summary>
/// <remarks>
/// Metadata only, and deliberately minimal: enough for the agent to decide whether
/// to collect, and nothing more. No envelope, no ciphertext, no key material of any
/// kind -- an agent that already holds the machine's disk does not need any of it
/// back, and a status endpoint is a poor place to hand out what it does not need.
/// </remarks>
/// <param name="Escrowed">
/// Whether a live escrow exists. The agent uses this to skip retrieval entirely, so
/// a machine already filed never reads its recovery password again.
/// </param>
/// <param name="State">
/// The server's view of this protector: <c>Pending</c>, <c>Escrowed</c>,
/// <c>Failed</c> or <c>RetryExhausted</c>.
/// </param>
/// <param name="Due">
/// Whether an attempt is owed right now.
/// <para>
/// <b>Decided by the server, not the agent.</b> The retry schedule lives in the
/// database precisely so that restarting an agent -- or restarting it in a loop --
/// cannot hand it a fresh budget of attempts and let it hammer Windows and the API.
/// An agent that is told <c>false</c> does not read a recovery password.
/// </para>
/// </param>
public sealed record BitLockerEscrowStatusItem(
    string VolumeDeviceIdentifier,
    string KeyProtectorId,
    bool Escrowed,
    DateTimeOffset? EscrowedAt,
    string State,
    bool Due,
    DateTimeOffset? NextAttemptAt);

/// <summary>
/// Response for <c>GET /agent/v1/bitlocker/escrow-status</c>.
/// </summary>
/// <param name="Eligible">
/// Whether this device may escrow automatically at all. False when the credential
/// carries no pinned sealing-key fingerprint -- the state every device enrolled
/// before automatic escrow is in, until it re-enrolls.
/// </param>
/// <param name="SealingKeyFingerprint">
/// The fingerprint this device is pinned to, echoed so the agent can detect a
/// mismatch against what it holds. Public material: it identifies a key and
/// decrypts nothing.
/// </param>
/// <param name="SealingPublicKey">
/// Base64 SPKI of the key to seal to. The agent verifies this against the
/// fingerprint it pinned at enrollment and refuses to use it otherwise, so a
/// substituted key here yields nothing: it is offered, not trusted.
/// </param>
/// <param name="Protectors">
/// The protectors this device has <em>reported through inventory</em>, which is
/// what makes them legitimate escrow targets. A protector Windows has only just
/// created becomes a target after the next inventory upload, not before -- the
/// server would refuse an escrow for a protector it has never seen.
/// </param>
public sealed record BitLockerEscrowStatusResponse(
    bool Eligible,
    string? SealingKeyFingerprint,
    string? SealingPublicKey,
    IReadOnlyList<BitLockerEscrowStatusItem> Protectors);
