namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Exchanges a one-time secret reference for the secret itself, over the agent's
/// authenticated channel.
/// </summary>
/// <remarks>
/// <para>
/// Task payloads are persisted server-side, so a password never travels in one.
/// The payload carries only a reference; this redeems it exactly once, immediately
/// before use, and the plaintext exists only for the duration of the call.
/// </para>
/// <para>
/// A null result means the reference was expired, already used, or not this
/// device's. Implementations must never retry indefinitely and must never log,
/// cache or persist the redeemed value.
/// </para>
/// </remarks>
public interface ISecretRedeemer
{
    Task<string?> RedeemAsync(string secretReference, CancellationToken cancellationToken = default);
}
