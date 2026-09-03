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
        DateTimeOffset collectedAt,
        string? installationScope = null,
        string? installedForUser = null,
        string? productCode = null)
    {
        DeviceId = Guard.NotEmpty(deviceId);
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name), maxLength: 384);
        Version = Guard.OptionalMaxLength(version, 128);
        Publisher = Guard.OptionalMaxLength(publisher, 256);
        InstallDate = Guard.OptionalMaxLength(installDate, 32);
        InstallLocation = Guard.OptionalMaxLength(installLocation, 512);
        Architecture = Guard.OptionalMaxLength(architecture, 16);
        CollectedAt = collectedAt;
        InstallationScope = Guard.OptionalMaxLength(installationScope, 16);
        InstalledForUser = Guard.OptionalMaxLength(installedForUser, 256);
        ProductCode = Guard.OptionalMaxLength(productCode, 64);
    }

    public Guid DeviceId { get; private set; }

    public string Name { get; private set; }

    public string? Version { get; private set; }

    public string? Publisher { get; private set; }

    public string? InstallDate { get; private set; }

    public string? InstallLocation { get; private set; }

    /// <summary>
    /// Which uninstall registry view the entry was found in, not the binary's
    /// architecture.
    /// </summary>
    /// <remarks>
    /// Chrome, Edge and Brave are 64-bit yet register under WOW6432Node and so
    /// report <c>x86</c>. The console labels this as the registry view rather
    /// than claiming an architecture the platform has not actually determined.
    /// </remarks>
    public string? Architecture { get; private set; }

    public DateTimeOffset CollectedAt { get; private set; }

    /// <summary>
    /// <c>Machine</c> for an all-users install, <c>User</c> for a per-user one;
    /// null when reported by an agent older than 1.5.0, which could not tell.
    /// </summary>
    public string? InstallationScope { get; private set; }

    /// <summary>
    /// The account a per-user install belongs to; null for machine-wide installs
    /// and for agents older than 1.5.0. The same product installed for two people
    /// is two rows, because it is two installations.
    /// </summary>
    public string? InstalledForUser { get; private set; }

    /// <summary>
    /// The Windows Installer product code, when the application has one. Matches
    /// <c>SoftwarePackage.MsiProductCode</c>, so an installed application can be
    /// related to an approved package.
    /// </summary>
    public string? ProductCode { get; private set; }
}
