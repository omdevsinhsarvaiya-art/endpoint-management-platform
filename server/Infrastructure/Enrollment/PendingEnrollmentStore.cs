using System.Security.Cryptography;
using System.Text.Json;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EndpointPlatform.Infrastructure.Enrollment;

/// <summary>
/// Holds enrollment requests between an agent asking and an administrator deciding.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="EphemeralSecretStore"/> deliberately: same Redis namespace
/// convention, same 15-minute lifetime, same seal-at-rest, same atomic single-use
/// redemption. Enrollment did not need a second storage idiom.
/// </para>
/// <para>
/// <b>The agent proves possession, it does not present a bearer token.</b> The agent
/// generates a 256-bit request secret and sends only its SHA-256 hash. The server
/// stores the hash and never learns the secret until the claim, where the secret is
/// hashed again and compared. A dump of Redis therefore yields nothing that can
/// claim a credential, and nothing sensitive is ever written to the endpoint's disk
/// by an installer or left in a Downloads folder.
/// </para>
/// </remarks>
public sealed class PendingEnrollmentStore(
    IConnectionMultiplexer redis,
    ISecretProtector protector,
    ILogger<PendingEnrollmentStore> logger)
{
    /// <summary>Dedicated key namespace, so these never collide with cache or secret entries.</summary>
    private const string KeyPrefix = "endpointplatform:enroll-request:";

    private readonly IConnectionMultiplexer _redis = redis ?? throw new ArgumentNullException(nameof(redis));
    private readonly ISecretProtector _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    private readonly ILogger<PendingEnrollmentStore> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Derives the public request id from the agent's private request secret.
    /// </summary>
    /// <remarks>
    /// SHA-256 with no salt on purpose: the input is already 256 bits of entropy, so
    /// there is nothing to brute-force and the derivation must be reproducible by the
    /// server from the secret alone at claim time.
    /// </remarks>
    public static string DeriveRequestId(string requestSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestSecret);
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(requestSecret));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Records a machine asking to be managed. Returns false when the store is
    /// unreachable, which the caller must surface as a retryable failure rather than
    /// as a rejection.
    /// </summary>
    public async Task<bool> RequestAsync(
        string requestId, PendingEnrollment request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var database = _redis.GetDatabase();

            // NOT NX: an agent that restarts mid-wait re-sends the same request id and
            // must land on its existing record rather than being told it already
            // exists. Overwriting is safe because the id is derived from a secret only
            // that agent holds - but an already-approved request must not be reset to
            // pending, or approval could be undone by a retry.
            var key = KeyPrefix + requestId;
            var existing = await database.StringGetAsync(key);
            if (!existing.IsNullOrEmpty)
            {
                var current = Deserialize(existing!);
                if (current is not null && current.Status != PendingEnrollmentStatus.Pending)
                {
                    // Already decided; leave it alone and let the agent claim or stop.
                    return true;
                }
            }

            await database.StringSetAsync(
                key,
                JsonSerializer.Serialize(request, Json),
                PendingEnrollment.Lifetime);

            return true;
        }
        catch (RedisException ex)
        {
            _logger.LogError(ex, "Could not record an enrollment request; the pending store is unreachable.");
            return false;
        }
    }

    /// <summary>All requests currently awaiting or holding a decision.</summary>
    /// <remarks>
    /// Uses SCAN rather than KEYS so a large keyspace does not block Redis. The result
    /// is a point-in-time view: entries expire on their own, so a request may vanish
    /// between listing and approving, which the approve path treats as "gone" rather
    /// than as an error.
    /// </remarks>
    public async Task<IReadOnlyList<(string RequestId, PendingEnrollment Request)>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<(string, PendingEnrollment)>();

        try
        {
            var endpoints = _redis.GetEndPoints();
            if (endpoints.Length == 0)
            {
                return results;
            }

            var server = _redis.GetServer(endpoints[0]);
            var database = _redis.GetDatabase();

            await foreach (var key in server.KeysAsync(pattern: KeyPrefix + "*", pageSize: 250)
                               .WithCancellation(cancellationToken))
            {
                var value = await database.StringGetAsync(key);
                if (value.IsNullOrEmpty)
                {
                    continue;
                }

                var request = Deserialize(value!);
                if (request is not null)
                {
                    results.Add((((string?)key)![KeyPrefix.Length..], request));
                }
            }
        }
        catch (RedisException ex)
        {
            _logger.LogError(ex, "Could not list enrollment requests; the pending store is unreachable.");
        }

        return results.OrderBy(r => r.Item2.RequestedAt).ToList();
    }

    /// <summary>Reads one request, or null when it never existed or has expired.</summary>
    public async Task<PendingEnrollment?> FindAsync(
        string requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return null;
        }

        try
        {
            var value = await _redis.GetDatabase().StringGetAsync(KeyPrefix + requestId);
            return value.IsNullOrEmpty ? null : Deserialize(value!);
        }
        catch (RedisException ex)
        {
            _logger.LogError(ex, "Could not read an enrollment request; the pending store is unreachable.");
            return null;
        }
    }

    /// <summary>
    /// Records an administrator's decision. The token secret is sealed before it is
    /// written, so Redis never holds it in the clear.
    /// </summary>
    /// <returns>
    /// The updated request, or null when it had already been decided, had expired, or
    /// the store is unreachable. A null return must never be reported as success:
    /// double approval and approve-after-expiry are exactly what this prevents.
    /// </returns>
    public async Task<PendingEnrollment?> DecideAsync(
        string requestId,
        PendingEnrollmentStatus decision,
        Guid? organizationId,
        string? tokenSecret,
        string? approvedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        if (decision == PendingEnrollmentStatus.Pending)
        {
            throw new ArgumentException("A decision cannot be 'Pending'.", nameof(decision));
        }

        try
        {
            var database = _redis.GetDatabase();
            var key = KeyPrefix + requestId;

            var current = await database.StringGetAsync(key);
            if (current.IsNullOrEmpty)
            {
                return null; // expired or never existed
            }

            var request = Deserialize(current!);
            if (request is null || request.Status != PendingEnrollmentStatus.Pending)
            {
                return null; // already decided
            }

            var updated = request with
            {
                Status = decision,
                OrganizationId = organizationId,
                SealedTokenSecret = tokenSecret is null ? null : _protector.Protect(tokenSecret),
                ApprovedBy = approvedBy,
            };

            // Conditional on the value we read, so two administrators clicking Approve
            // at the same moment cannot both succeed.
            var transaction = database.CreateTransaction();
            transaction.AddCondition(Condition.StringEqual(key, current));
            _ = transaction.StringSetAsync(key, JsonSerializer.Serialize(updated, Json), PendingEnrollment.Lifetime);

            return await transaction.ExecuteAsync() ? updated : null;
        }
        catch (RedisException ex)
        {
            _logger.LogError(ex, "Could not record an enrollment decision; the pending store is unreachable.");
            return null;
        }
    }

    /// <summary>
    /// Redeems an approved request exactly once, returning the unsealed enrollment
    /// token secret for the caller to feed to the existing enrollment path.
    /// </summary>
    /// <remarks>
    /// Approved requests are removed atomically with GETDEL, so a replayed claim
    /// cannot obtain a second credential. A request that is still pending is left in
    /// place and reported as such, so the agent keeps waiting rather than losing its
    /// place in the queue.
    /// </remarks>
    public async Task<ClaimOutcome> ClaimAsync(
        string requestSecret, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestSecret))
        {
            return ClaimOutcome.NotFound();
        }

        var requestId = DeriveRequestId(requestSecret);

        try
        {
            var database = _redis.GetDatabase();
            var key = KeyPrefix + requestId;

            var value = await database.StringGetAsync(key);
            if (value.IsNullOrEmpty)
            {
                return ClaimOutcome.NotFound();
            }

            var request = Deserialize(value!);
            if (request is null)
            {
                return ClaimOutcome.NotFound();
            }

            switch (request.Status)
            {
                case PendingEnrollmentStatus.Pending:
                    return ClaimOutcome.StillPending();

                case PendingEnrollmentStatus.Rejected:
                    // Remove it, so a rejected machine stops polling a dead request
                    // instead of retrying until the TTL runs out.
                    await database.KeyDeleteAsync(key);
                    return ClaimOutcome.Rejected();

                case PendingEnrollmentStatus.Approved:
                    // Atomic read-and-delete: two concurrent claims cannot both win.
                    var claimed = await database.StringGetDeleteAsync(key);
                    if (claimed.IsNullOrEmpty)
                    {
                        return ClaimOutcome.NotFound();
                    }

                    var approved = Deserialize(claimed!);
                    if (approved?.SealedTokenSecret is null || approved.OrganizationId is null)
                    {
                        _logger.LogError(
                            "Approved enrollment request {RequestId} carried no token; refusing to issue a credential.",
                            requestId);
                        return ClaimOutcome.NotFound();
                    }

                    try
                    {
                        return ClaimOutcome.Ready(_protector.Unprotect(approved.SealedTokenSecret), approved);
                    }
                    catch (CryptographicException ex)
                    {
                        // Almost always a SecretProtection key mismatch between hosts.
                        _logger.LogError(ex, "An approved enrollment token could not be unsealed.");
                        return ClaimOutcome.NotFound();
                    }

                default:
                    return ClaimOutcome.NotFound();
            }
        }
        catch (RedisException ex)
        {
            _logger.LogError(ex, "Could not claim an enrollment request; the pending store is unreachable.");
            return ClaimOutcome.Unavailable();
        }
    }

    private static PendingEnrollment? Deserialize(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<PendingEnrollment>(value, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>What a claim attempt found.</summary>
public sealed record ClaimOutcome(
    ClaimStatus Status,
    string? EnrollmentTokenSecret = null,
    PendingEnrollment? Request = null)
{
    public static ClaimOutcome Ready(string tokenSecret, PendingEnrollment request) =>
        new(ClaimStatus.Approved, tokenSecret, request);

    public static ClaimOutcome StillPending() => new(ClaimStatus.Pending);

    public static ClaimOutcome Rejected() => new(ClaimStatus.Rejected);

    public static ClaimOutcome NotFound() => new(ClaimStatus.NotFound);

    public static ClaimOutcome Unavailable() => new(ClaimStatus.Unavailable);
}

public enum ClaimStatus
{
    /// <summary>Approved and redeemed; the caller may now issue a credential.</summary>
    Approved = 0,

    /// <summary>Still waiting on an administrator. The agent should keep polling.</summary>
    Pending = 1,

    /// <summary>Refused by an administrator. The agent must stop polling this request.</summary>
    Rejected = 2,

    /// <summary>Unknown, already claimed, or expired.</summary>
    NotFound = 3,

    /// <summary>The store is unreachable; retryable, and not a rejection.</summary>
    Unavailable = 4,
}
