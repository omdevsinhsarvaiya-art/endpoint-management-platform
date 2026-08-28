using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EndpointPlatform.Infrastructure.BitLocker;

/// <param name="Allowed">Whether this attempt may proceed.</param>
/// <param name="Scope">Which limit was hit: "user" or "device". Null when allowed.</param>
/// <param name="RetryAfterSeconds">Roughly how long until the window rolls.</param>
public sealed record RevealRateLimitDecision(bool Allowed, string? Scope, int RetryAfterSeconds);

/// <summary>
/// Limits how often recovery keys may be revealed.
/// </summary>
/// <remarks>
/// <para>
/// Two independent windows, both required. The <b>per-user</b> limit bounds what a
/// single stolen session can extract. The <b>per-device</b> limit bounds what a
/// group of colluding or compromised accounts can extract about one machine, which
/// the per-user limit alone would not.
/// </para>
/// <para>
/// <b>A successful reveal does not reset either counter</b>, which is the whole
/// point. A limiter that reset on success would let an attacker who is guessing
/// correctly proceed without limit, and it is exactly the successful reveals that
/// need bounding -- a failed reveal yields nothing.
/// </para>
/// <para>
/// Counted in Redis with INCR plus an expiry set on first increment, so the window
/// is atomic across instances and needs no cleanup. Redis being a cache is
/// acceptable here: losing it resets the counters, which is a availability-favouring
/// failure the audit trail still records. It is a brake, not the access control --
/// the permission, the device scope and the step-up password are.
/// </para>
/// <para>
/// <b>No secret, ciphertext or escrow id appears in a key, a log line or a
/// response.</b> Keys are derived from the user id and the device id only.
/// </para>
/// </remarks>
public sealed class RevealRateLimiter(
    IConnectionMultiplexer redis,
    TimeProvider timeProvider,
    ILogger<RevealRateLimiter> logger)
{
    /// <summary>Attempts allowed per user, and per device, per window.</summary>
    public const int MaxAttemptsPerWindow = 5;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly IConnectionMultiplexer _redis = redis ?? throw new ArgumentNullException(nameof(redis));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<RevealRateLimiter> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Counts this attempt against both windows and says whether it may proceed.
    /// </summary>
    /// <remarks>
    /// The attempt is counted before it is judged, so an attempt that trips the
    /// limit still consumes budget. Counting only permitted attempts would let a
    /// caller sit exactly at the boundary indefinitely.
    /// </remarks>
    public async Task<RevealRateLimitDecision> TryConsumeAsync(
        Guid userId, Guid deviceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var db = _redis.GetDatabase();

            var userCount = await IncrementAsync(db, Key("user", userId));
            var deviceCount = await IncrementAsync(db, Key("device", deviceId));

            if (userCount > MaxAttemptsPerWindow)
            {
                _logger.LogWarning(
                    "Recovery-key reveal refused: user {UserId} has made {Count} attempts in the window.",
                    userId, userCount);

                return new RevealRateLimitDecision(false, "user", (int)Window.TotalSeconds);
            }

            if (deviceCount > MaxAttemptsPerWindow)
            {
                _logger.LogWarning(
                    "Recovery-key reveal refused: device {DeviceId} has had {Count} attempts in the window.",
                    deviceId, deviceCount);

                return new RevealRateLimitDecision(false, "device", (int)Window.TotalSeconds);
            }

            return new RevealRateLimitDecision(true, null, 0);
        }
        catch (RedisException ex)
        {
            // Fail open, deliberately and narrowly. The limiter is a brake on an
            // operation that has already passed a permission check, a device scope
            // check and a password re-verification, and every attempt is audited
            // regardless. Failing closed would make a Redis outage lock an
            // administrator out of the recovery key for a machine that will not
            // boot -- the exact emergency this feature exists to serve.
            _logger.LogError(ex,
                "Recovery-key reveal rate limiter unavailable; allowing the attempt. "
                + "The permission, scope and step-up checks still applied.");

            return new RevealRateLimitDecision(true, null, 0);
        }
    }

    private async Task<long> IncrementAsync(IDatabase db, RedisKey key)
    {
        var count = await db.StringIncrementAsync(key);

        // Only the first increment sets the expiry, which is what makes this a
        // fixed window rather than one that slides forward on every attempt and
        // never expires under sustained abuse.
        if (count == 1)
        {
            await db.KeyExpireAsync(key, Window);
        }

        return count;
    }

    /// <summary>
    /// Window-stamped so counters roll without a sweeper. Contains only an
    /// identifier - never an escrow id, a protector id or any secret.
    /// </summary>
    private RedisKey Key(string scope, Guid id)
    {
        var window = _timeProvider.GetUtcNow().ToUnixTimeSeconds() / (long)Window.TotalSeconds;
        return (RedisKey)$"escrow:reveal:{scope}:{id:N}:{window}";
    }
}
