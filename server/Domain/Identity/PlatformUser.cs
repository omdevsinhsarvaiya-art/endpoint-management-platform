using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Identity;

/// <summary>
/// A human administrator of the platform who signs in to the Admin API.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately named <c>PlatformUser</c>, not <c>User</c>. "User" is ambiguous in
/// this product because the platform also manages Windows <em>local</em> user
/// accounts on endpoints, which are an entirely different thing with a different
/// trust model. Keeping the names distinct prevents the two from ever being
/// conflated in code, in the API surface or in an audit record.
/// </para>
/// <para>
/// This type never holds a plaintext password. <see cref="PasswordHash"/> holds an
/// encoded hash produced by the credential hasher in the infrastructure layer, and
/// it is excluded from serialisation and from logging.
/// </para>
/// </remarks>
public sealed class PlatformUser : AuditableEntity
{
    private readonly List<PlatformUserRole> _roles = [];

    private PlatformUser()
    {
        Email = null!;
        NormalizedEmail = null!;
        DisplayName = null!;
        SecurityStamp = null!;
    }

    public PlatformUser(Guid organizationId, string email, string displayName)
    {
        OrganizationId = Guard.NotEmpty(organizationId);
        Email = Guard.NotNullOrWhiteSpace(email, nameof(email), maxLength: 254);
        NormalizedEmail = Email.ToUpperInvariant();
        DisplayName = Guard.NotNullOrWhiteSpace(displayName, nameof(displayName), maxLength: 200);
        Status = PlatformUserStatus.Invited;
        SecurityStamp = Guid.CreateVersion7().ToString("N");
    }

    public Guid OrganizationId { get; private set; }

    public Organization? Organization { get; private set; }

    public string Email { get; private set; }

    /// <summary>Upper-invariant form of <see cref="Email"/>, used for the uniqueness index and lookups.</summary>
    public string NormalizedEmail { get; private set; }

    public string DisplayName { get; private set; }

    /// <summary>Encoded password hash. Never a plaintext password, never logged, never serialised.</summary>
    public string? PasswordHash { get; private set; }

    public DateTimeOffset? PasswordUpdatedAt { get; private set; }

    /// <summary>
    /// Rotated whenever credentials or role assignments change. Issued access tokens
    /// carry the stamp so that a disabled or re-permissioned account's outstanding
    /// tokens stop validating immediately instead of at natural expiry.
    /// </summary>
    public string SecurityStamp { get; private set; }

    public PlatformUserStatus Status { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    public int FailedSignInCount { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    /// <summary>
    /// A built-in account created by seeding rather than by an administrator.
    /// System accounts cannot be deleted, only disabled.
    /// </summary>
    public bool IsSystemAccount { get; private set; }

    /// <summary>
    /// Whether this administrator's authority spans every device in the organization.
    /// </summary>
    /// <remarks>
    /// Deny-by-default: a new administrator has this false and no
    /// <see cref="AdminDeviceScope"/> rows, so their permissions reach no device until
    /// scope is granted explicitly. "No scope" therefore means "nothing", never
    /// "everything" — the inverse would make every future account silently omnipotent.
    /// </remarks>
    public bool HasAllDeviceScope { get; private set; }

    /// <summary>Grants authority over every device in the organization.</summary>
    public void GrantAllDeviceScope() => HasAllDeviceScope = true;

    /// <summary>Revokes organization-wide authority, leaving only explicit group scopes.</summary>
    public void RevokeAllDeviceScope() => HasAllDeviceScope = false;

    public IReadOnlyCollection<PlatformUserRole> Roles => _roles.AsReadOnly();

    public void MarkAsSystemAccount() => IsSystemAccount = true;

    /// <summary>
    /// Stores an already-hashed credential. The domain never sees the plaintext, so
    /// there is no code path here that could accidentally persist or log one.
    /// </summary>
    public void SetPasswordHash(string encodedHash, DateTimeOffset now)
    {
        PasswordHash = Guard.NotNullOrWhiteSpace(encodedHash, nameof(encodedHash), maxLength: 512);
        PasswordUpdatedAt = now;
        FailedSignInCount = 0;
        LockedUntil = null;
        RotateSecurityStamp();

        if (Status == PlatformUserStatus.Invited)
        {
            Status = PlatformUserStatus.Active;
        }
    }

    public void RecordSuccessfulSignIn(DateTimeOffset now)
    {
        LastLoginAt = now;
        FailedSignInCount = 0;
        LockedUntil = null;
    }

    public void RecordFailedSignIn(DateTimeOffset now, int lockoutThreshold, TimeSpan lockoutDuration)
    {
        FailedSignInCount++;

        if (FailedSignInCount >= lockoutThreshold)
        {
            Status = PlatformUserStatus.Locked;
            LockedUntil = now + lockoutDuration;
        }
    }

    public bool IsLockedOut(DateTimeOffset now) =>
        Status == PlatformUserStatus.Locked && LockedUntil is { } until && until > now;

    public void Disable()
    {
        Status = PlatformUserStatus.Disabled;
        RotateSecurityStamp();
    }

    public void Enable()
    {
        Status = PasswordHash is null ? PlatformUserStatus.Invited : PlatformUserStatus.Active;
        FailedSignInCount = 0;
        LockedUntil = null;
        RotateSecurityStamp();
    }

    public void AssignRole(Guid roleId)
    {
        Guard.NotEmpty(roleId);

        if (_roles.Any(r => r.RoleId == roleId))
        {
            return;
        }

        _roles.Add(new PlatformUserRole(Id, roleId));
        RotateSecurityStamp();
    }

    public void RemoveRole(Guid roleId)
    {
        var removed = _roles.RemoveAll(r => r.RoleId == roleId);

        if (removed > 0)
        {
            RotateSecurityStamp();
        }
    }

    private void RotateSecurityStamp() => SecurityStamp = Guid.CreateVersion7().ToString("N");
}
