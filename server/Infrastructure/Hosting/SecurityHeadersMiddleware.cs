using Microsoft.AspNetCore.Http;

namespace EndpointPlatform.Infrastructure.Hosting;

/// <summary>
/// Applies baseline response security headers to both APIs.
/// </summary>
/// <remarks>
/// <para>
/// These are JSON APIs, not HTML applications, so the headers here target the two
/// realistic risks: a browser being tricked into rendering or sniffing an API
/// response, and API responses leaking into referrers or caches.
/// </para>
/// <para>
/// HSTS is deliberately not set here - it is configured per host via
/// <c>UseHsts</c> so that a development instance on plain HTTP does not poison the
/// browser's HSTS cache for localhost. Full hardening (CSP for the dashboard's own
/// origin, permissions policy, HSTS preload) belongs to Phase 15.
/// </para>
/// </remarks>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var headers = context.Response.Headers;

        // Refuse to let a browser guess a content type; an API response must never
        // be reinterpreted as HTML or script.
        headers["X-Content-Type-Options"] = "nosniff";

        // No API response should ever be framed.
        headers["X-Frame-Options"] = "DENY";

        // A restrictive CSP is meaningful even for JSON: it neutralises the classic
        // "browse to an API endpoint and get script executed" class of bug.
        headers["Content-Security-Policy"] =
            "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

        // Do not leak API paths (which contain device and user identifiers) to
        // third-party origins via the Referer header.
        headers["Referrer-Policy"] = "no-referrer";

        // Responses are per-principal; shared caches must not retain them.
        headers["Cache-Control"] = "no-store, no-cache, must-revalidate";

        await _next(context);
    }
}
