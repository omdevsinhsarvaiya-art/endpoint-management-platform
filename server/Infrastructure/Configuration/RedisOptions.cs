using System.ComponentModel.DataAnnotations;

namespace EndpointPlatform.Infrastructure.Configuration;

/// <summary>
/// Redis connection settings.
/// </summary>
/// <remarks>
/// Redis is used for caching, rate-limit counters and (later) SignalR backplane and
/// task queueing. It is treated as untrusted-for-durability: anything that must
/// survive a restart lives in PostgreSQL. Losing Redis degrades performance and
/// real-time delivery, it must never lose an audit record or a device credential.
/// </remarks>
public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    [Required(AllowEmptyStrings = false, ErrorMessage =
        "Redis:ConnectionString is not configured. Set ENDPOINTPLATFORM_Redis__ConnectionString " +
        "or copy infra/.env.example to infra/.env. See docs/development.md.")]
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>
    /// Key prefix applied to every cache entry. Keeps the Admin API, the Agent API
    /// and any other tenant of the same Redis instance from colliding.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[a-z0-9:_-]+$", ErrorMessage = "Redis:InstanceName may contain only a-z, 0-9, ':', '_' and '-'.")]
    public string InstanceName { get; init; } = "endpointplatform:";

    [Range(100, 30_000)]
    public int ConnectTimeoutMs { get; init; } = 5_000;

    /// <summary>
    /// When true the host still starts if Redis is unreachable, and the readiness
    /// health check reports Degraded. Appropriate for development; in deployment,
    /// leave it false so a broken cache tier is caught by the orchestrator.
    /// </summary>
    public bool AbortOnConnectFail { get; init; }
}
