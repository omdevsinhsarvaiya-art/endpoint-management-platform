namespace EndpointPlatform.Api.Security;

/// <summary>
/// CSRF defence for the cookie-authenticated flow.
/// </summary>
/// <remarks>
/// <para>
/// The session cookie is <c>SameSite=Strict</c>, which modern browsers honour;
/// this middleware is the defence-in-depth layer behind it: any mutating request
/// that presents the session cookie (rather than an explicit Authorization
/// header) must also carry <c>X-Requested-With: XMLHttpRequest</c>. A hostile
/// site cannot attach that header cross-origin without a CORS preflight the API
/// will not grant, while the dashboard's own fetch wrapper always sends it.
/// </para>
/// <para>
/// Bearer-authenticated requests are exempt: an attacker who can set an
/// Authorization header already runs code in a first-party context, which is
/// outside CSRF's threat model.
/// </para>
/// </remarks>
public sealed class CsrfProtectionMiddleware(RequestDelegate next)
{
    public const string RequiredHeader = "X-Requested-With";
    public const string RequiredHeaderValue = "XMLHttpRequest";

    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Get, HttpMethods.Head, HttpMethods.Options,
    };

    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(HttpContext context)
    {
        var usesCookieAuth =
            context.Request.Cookies.ContainsKey(AdminAuthenticationHandler.SessionCookieName)
            && string.IsNullOrEmpty(context.Request.Headers.Authorization);

        if (usesCookieAuth
            && !SafeMethods.Contains(context.Request.Method)
            && !string.Equals(
                context.Request.Headers[RequiredHeader],
                RequiredHeaderValue,
                StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                title = "Missing anti-CSRF header.",
                detail = $"Mutating requests authenticated by cookie must send {RequiredHeader}: {RequiredHeaderValue}.",
            });
            return;
        }

        await _next(context);
    }
}
