using System.Net;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Infrastructure.Hosting;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;

namespace EndpointPlatform.Infrastructure.Auditing;

/// <summary>
/// The one supported way to record an audit event.
/// </summary>
/// <remarks>
/// <para>
/// Centralised so that request context (source IP, user agent, correlation id)
/// is captured uniformly and so no call site hand-assembles an entry and forgets
/// a field. The writer only stages the entry; it commits with the operation's own
/// <c>SaveChanges</c>, so an audited action and its audit record succeed or fail
/// as one transaction — no action without its record, no record for an action
/// that rolled back.
/// </para>
/// <para>
/// Failure/denial events that must be recorded even though the operation itself
/// is rolled back or never started use <see cref="WriteImmediatelyAsync"/>.
/// </para>
/// </remarks>
public sealed class AuditWriter(
    EndpointPlatformDbContext dbContext,
    TimeProvider timeProvider,
    ICorrelationIdAccessor correlationIdAccessor,
    IHttpContextAccessor httpContextAccessor)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ICorrelationIdAccessor _correlationIdAccessor = correlationIdAccessor
        ?? throw new ArgumentNullException(nameof(correlationIdAccessor));

    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor
        ?? throw new ArgumentNullException(nameof(httpContextAccessor));

    /// <summary>
    /// Stages an audit entry in the current unit of work. It persists when the
    /// surrounding operation calls <c>SaveChanges</c>.
    /// </summary>
    public AuditLogEntry Stage(
        Guid organizationId,
        AuditActorType actorType,
        Guid? actorId,
        string actorDisplay,
        string action,
        AuditResult result,
        Action<AuditLogEntry.AuditLogEntryBuilder>? configure = null)
    {
        var builder = AuditLogEntry.For(
            organizationId,
            _timeProvider.GetUtcNow(),
            actorType,
            actorId,
            actorDisplay,
            action,
            result);

        var httpContext = _httpContextAccessor.HttpContext;
        builder.FromRequest(
            GetClientIp(httpContext),
            httpContext?.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null,
            _correlationIdAccessor.CorrelationId);

        configure?.Invoke(builder);

        var entry = builder.Build();
        _dbContext.AuditLogEntries.Add(entry);
        return entry;
    }

    /// <summary>
    /// Stages and saves in its own right — for refusals and failures, where the
    /// operation has nothing else to commit but the attempt must still be recorded.
    /// </summary>
    public async Task<AuditLogEntry> WriteImmediatelyAsync(
        Guid organizationId,
        AuditActorType actorType,
        Guid? actorId,
        string actorDisplay,
        string action,
        AuditResult result,
        Action<AuditLogEntry.AuditLogEntryBuilder>? configure = null,
        CancellationToken cancellationToken = default)
    {
        var entry = Stage(organizationId, actorType, actorId, actorDisplay, action, result, configure);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return entry;
    }

    private static IPAddress? GetClientIp(HttpContext? httpContext)
    {
        // Connection.RemoteIpAddress only. X-Forwarded-For is attacker-writable
        // unless a trusted proxy is configured; when a reverse proxy is introduced
        // (Phase 15), ForwardedHeaders middleware rewrites RemoteIpAddress and this
        // code stays correct without change.
        return httpContext?.Connection.RemoteIpAddress;
    }
}
