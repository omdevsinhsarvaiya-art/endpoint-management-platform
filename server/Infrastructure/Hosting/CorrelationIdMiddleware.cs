using System.Buffers;
using Microsoft.AspNetCore.Http;
using Serilog.Context;

namespace EndpointPlatform.Infrastructure.Hosting;

/// <summary>
/// Assigns every request a correlation id, echoes it back, and pushes it into the
/// log context so that every log line and audit entry produced while handling the
/// request can be tied together.
/// </summary>
/// <remarks>
/// <para>
/// A client-supplied <c>X-Correlation-Id</c> is accepted so a trace can span the
/// dashboard, the API and the agent, but it is treated as untrusted input: it is
/// length-limited and restricted to an unambiguous character set before being used.
/// Echoing an arbitrary client string into a response header and into log files is
/// how header-injection and log-forging bugs happen.
/// </para>
/// </remarks>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    /// <summary>
    /// Correlation ids may contain ASCII letters, digits, '-', '_' and '.' only.
    /// That excludes CR, LF and every other control character, which is what makes
    /// echoing the value into a header and a log line safe.
    /// </summary>
    private static readonly SearchValues<char> AllowedCharacters = SearchValues.Create(
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.");

    public async Task InvokeAsync(HttpContext context, CorrelationIdAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(accessor);

        var correlationId = ResolveCorrelationId(context);
        accessor.Set(correlationId);

        context.Response.Headers[CorrelationId.HeaderName] = correlationId;

        using (LogContext.PushProperty(CorrelationId.LogPropertyName, correlationId))
        {
            await _next(context);
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(CorrelationId.HeaderName, out var values))
        {
            var candidate = values.ToString();

            if (IsAcceptable(candidate))
            {
                return candidate;
            }
        }

        // TraceIdentifier is server-generated and already safe.
        return context.TraceIdentifier;
    }

    internal static bool IsAcceptable(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= CorrelationId.MaxLength
        && !value.AsSpan().ContainsAnyExcept(AllowedCharacters);
}

/// <summary>
/// Scoped holder for the current request's correlation id.
/// </summary>
/// <remarks>
/// Registered as scoped so each request gets its own instance; this is per-request
/// state resolved through DI, not ambient global state.
/// </remarks>
public sealed class CorrelationIdAccessor : ICorrelationIdAccessor
{
    private string? _correlationId;

    /// <summary>
    /// The current correlation id. Falls back to a generated value for work that
    /// happens outside an HTTP request (background jobs, seeding), so audit entries
    /// written there are still traceable.
    /// </summary>
    public string CorrelationId => _correlationId ??= Guid.CreateVersion7().ToString("N");

    internal void Set(string correlationId) => _correlationId = correlationId;
}
