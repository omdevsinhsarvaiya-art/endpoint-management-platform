namespace EndpointPlatform.Domain.Identity;

/// <summary>
/// Lifecycle state of a platform administrator account.
/// </summary>
/// <remarks>
/// Stored as text in PostgreSQL rather than an ordinal, so that reordering or
/// inserting a member can never silently reinterpret existing rows.
/// </remarks>
public enum PlatformUserStatus
{
    /// <summary>Created but has not completed first sign-in / credential setup.</summary>
    Invited = 0,

    Active = 1,

    /// <summary>Administratively disabled. Cannot authenticate; retained for audit.</summary>
    Disabled = 2,

    /// <summary>Temporarily locked by the platform after repeated failed sign-ins.</summary>
    Locked = 3,
}
