using Microsoft.AspNetCore.Http.Features;

namespace EndpointPlatform.Api.Security;

/// <summary>
/// Per-request raising of Kestrel's body-size cap for the two installer-upload
/// endpoints.
/// </summary>
/// <remarks>
/// <para>
/// Kestrel enforces its own request-body limit (default 30,000,000 bytes) when
/// the body is read — before any handler-level <c>file.Length</c> check can
/// run. The application ceilings (250 MB agent release, 2 GB package) were
/// therefore decorative for anything past ~28.6 MiB: the very first real agent
/// MSI, at 29.4 MiB, was refused with 413 by a layer the tests never exercised,
/// because the in-process test server does not enforce Kestrel limits at all.
/// </para>
/// <para>
/// The raise is per-request and endpoint-local: every other endpoint keeps the
/// 30 MB default, so this cannot quietly open the rest of the API to huge
/// bodies. The handler's own size check remains the business limit; this only
/// stops Kestrel from vetoing it first.
/// </para>
/// </remarks>
public static class RequestBodyLimits
{
    /// <summary>
    /// Multipart framing overhead allowance on top of the payload ceiling:
    /// boundaries, part headers and the form fields riding alongside the file.
    /// </summary>
    public const long MultipartOverheadBytes = 1L * 1024 * 1024;

    /// <summary>
    /// Lifts this request's Kestrel body cap to <paramref name="payloadCeiling"/>
    /// plus multipart overhead. Must run before the body is read; a feature that
    /// is absent (in-process test server) or already read-only is left alone.
    /// </summary>
    public static void AllowUploadOf(HttpContext httpContext, long payloadCeiling)
    {
        var feature = httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = payloadCeiling + MultipartOverheadBytes;
        }
    }
}
