using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// One installed application on a managed device, as last reported by its agent.
/// Replaced wholesale per inventory upload.
/// </summary>
public sealed class DeviceSoftware : AuditableEntity
{
    private DeviceSoftware()
    {
        Name = null!;
    }

    public DeviceSoftware(
        Guid deviceId,
        string name,
        string? version,
        string? publisher,
        string? installDate,
        string? installLocation,
        string? architecture,
        DateTimeOffset collectedAt)
    {
        DeviceId = Guard.NotEmpty(deviceId);
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name), maxLength: 384);
        Version = Guard.OptionalMaxLength(version, 128);
        Publisher = Guard.OptionalMaxLength(publisher, 256);
        InstallDate = Guard.OptionalMaxLength(installDate, 32);
        InstallLocation = Guard.OptionalMaxLength(installLocation, 512);
        Architecture = Guard.OptionalMaxLength(architecture, 16);
        CollectedAt = collectedAt;
    }

    public Guid DeviceId { get; private set; }

    public string Name { get; private set; }

    public string? Version { get; private set; }

    public string? Publisher { get; private set; }

    public string? InstallDate { get; private set; }

    public string? InstallLocation { get; private set; }

    public string? Architecture { get; private set; }

    public DateTimeOffset CollectedAt { get; private set; }
}
