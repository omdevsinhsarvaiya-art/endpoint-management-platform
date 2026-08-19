using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EndpointPlatform.Infrastructure.Security;

/// <summary>
/// Holds a password for the few minutes between an operator submitting it and the
/// target agent redeeming it — and nowhere else, ever.
/// </summary>
/// <remarks>
/// <para>
/// Task payloads are persisted in PostgreSQL and mirrored into the audit trail, so a
/// password must never travel in one. Instead the secret is written here under an
/// unguessable reference, the persisted task carries only that reference, and the
/// agent exchanges it for the plaintext exactly once over its authenticated channel.
/// </para>
/// <para>
/// Four properties make this safe:
/// <list type="bullet">
///   <item><b>One-time</b>: redemption uses an atomic GETDEL, so a replayed reference
///   finds nothing. A stolen task row is therefore worthless after first use.</item>
///   <item><b>Short-lived</b>: entries expire on their own, so an unredeemed secret
///   (offline device, cancelled task) disappears without anyone cleaning up.</item>
///   <item><b>Device-bound</b>: the reference embeds the device id, and redemption
///   requires the redeeming device to match, so one agent cannot redeem another's
///   secret even with the reference in hand.</item>
///   <item><b>Encrypted at rest in Redis</b>: the value is AES-GCM sealed with a key
///   the API holds, so a Redis dump alone does not yield plaintext.</item>
/// </list>
/// </para>
/// <para>
/// Redis is already a dependency and is treated as a cache — losing it loses only
/// in-flight secrets, which safely fail the task rather than corrupting anything.
/// </para>
/// </remarks>
public sealed class EphemeralSecretStore(
    IConnectionMultiplexer redis,
    ISecretProtector protector,
    ILogger<EphemeralSecretStore> logger)
{
    /// <summary>Dedicated key namespace so these never collide with cache entries.</summary>
    private const string KeyPrefix = "endpointplatform:secret:";

    /// <summary>Long enough for an online agent to poll, short enough to bound exposure.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly IConnectionMultiplexer _redis = redis ?? throw new ArgumentNullException(nameof(redis));
    private readonly ISecretProtector _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    private readonly ILogger<EphemeralSecretStore> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Stores <paramref name="secret"/> for <paramref name="deviceId"/> and returns the
    /// reference to embed in the task payload. The plaintext is not logged or returned.
    /// </summary>
    public async Task<string?> StoreAsync(Guid deviceId, string secret, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        // The reference names the device so redemption can be bound to it, plus 256 bits
        // of entropy so it cannot be guessed or enumerated.
        var reference = $"{deviceId:N}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))}";
        var sealedSecret = _protector.Protect(secret);

        try
        {
            var database = _redis.GetDatabase();
            await database.StringSetAsync(KeyPrefix + reference, sealedSecret, Lifetime);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Fail closed and visibly: without somewhere safe to hold the secret we
            // refuse the operation rather than fall back to putting it in the task
            // payload, which is exactly what this store exists to prevent.
            _logger.LogError(ex, "Could not store an ephemeral secret; the operation is refused.");
            return null;
        }

        return reference;
    }

    /// <summary>
    /// Redeems a reference exactly once for the given device. Returns null when the
    /// reference is unknown, expired, already used, or belongs to another device.
    /// </summary>
    public async Task<string?> RedeemAsync(
        Guid deviceId, string reference, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        // Device binding: refuse before touching Redis if the reference is not this
        // device's, so one agent can never consume another's secret.
        var expectedPrefix = $"{deviceId:N}.";
        if (!reference.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            _logger.LogWarning("Device {DeviceId} attempted to redeem a secret reference bound to another device.", deviceId);
            return null;
        }

        var database = _redis.GetDatabase();

        // GETDEL: atomic read-and-delete, so a concurrent replay cannot both succeed.
        RedisValue sealedSecret;
        try
        {
            sealedSecret = await database.StringGetDeleteAsync(KeyPrefix + reference);
        }
        catch (RedisException ex)
        {
            // Fail safe: the task fails and can be re-issued. Never fall back to a
            // non-expiring or unencrypted path.
            _logger.LogError(ex, "Could not redeem an ephemeral secret; the task will fail and may be re-issued.");
            return null;
        }

        if (sealedSecret.IsNullOrEmpty)
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(sealedSecret!);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "An ephemeral secret could not be unsealed; treating it as unavailable.");
            return null;
        }
    }
}
