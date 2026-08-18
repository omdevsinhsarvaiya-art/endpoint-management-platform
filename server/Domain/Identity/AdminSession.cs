using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Identity;

/// <summary>
/// One authenticated Admin API session.
/// </summary>
/// <remarks>
/// <para>
/// Sessions are opaque server-side state, not JWTs: the client holds a 256-bit
/// random token (HttpOnly cookie), the server stores only its SHA-256. That makes
/// individual revocation a row update and leaves nothing self-validating in the
/// client's hands.
/// </para>
/// <para>
/// <see cref="SecurityStampSnapshot"/> pins the user's stamp at sign-in. Any
/// credential or role change rotates the user's stamp, which invalidates every
/// outstanding session immediately — a disabled administrator or revoked role
/// takes effect on the next request, not at token expiry.
/// </para>
/// </remarks>
public sealed class AdminSession : Entity
{
    public const int TokenHashLength = 64; // hex SHA-256

    private AdminSession()
    {
        TokenHash = null!;
        SecurityStampSnapshot = null!;
    }

    public AdminSession(
        Guid platformUserId,
        string tokenHash,
        string securityStampSnapshot,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        string? sourceIp,
        string? userAgent)
    {
        PlatformUserId = Guard.NotEmpty(platformUserId);
        TokenHash = Guard.NotNullOrWhiteSpace(tokenHash, nameof(tokenHash), maxLength: TokenHashLength);
        SecurityStampSnapshot = Guard.NotNullOrWhiteSpace(
            securityStampSnapshot, nameof(securityStampSnapshot), maxLength: 64);
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        LastActivityAt = createdAt;
        SourceIp = Guard.OptionalMaxLength(sourceIp, 64);
        UserAgent = Guard.OptionalMaxLength(userAgent, 512);
    }

    public Guid PlatformUserId { get; private set; }

    public PlatformUser? PlatformUser { get; private set; }

    /// <summary>SHA-256 of the session token; the token itself is never stored.</summary>
    public string TokenHash { get; private set; }

    public string SecurityStampSnapshot { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset LastActivityAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? SourceIp { get; private set; }

    public string? UserAgent { get; private set; }

    public bool IsUsable(DateTimeOffset now, string currentSecurityStamp) =>
        RevokedAt is null
        && now < ExpiresAt
        && string.Equals(SecurityStampSnapshot, currentSecurityStamp, StringComparison.Ordinal);

    public void Touch(DateTimeOffset now) => LastActivityAt = now;

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
