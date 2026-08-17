namespace EndpointPlatform.Domain.Devices;

/// <summary>Lifecycle state of a managed device.</summary>
/// <remarks>Stored as text; see the note on <see cref="Identity.PlatformUserStatus"/>.</remarks>
public enum DeviceStatus
{
    /// <summary>Enrolled and expected to heartbeat.</summary>
    Active = 0,

    /// <summary>
    /// Administratively retired. Its credential is revoked; a retired device that
    /// presents a valid-looking credential is a security signal, not a device.
    /// </summary>
    Retired = 1,
}
