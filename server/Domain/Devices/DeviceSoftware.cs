using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// One application on one device, as the device's last inventory reported it.
/// </summary>
/// <remarks>
/// The first ten members are the row as it has always been stored. The rest
/// arrived with application discovery (agent 1.9.0) and are null for rows an
/// older agent reported: an absent value means "the agent did not say", never
/// a default the server invented. Every string is bounded to the wire contract's
/// limit, which the Agent API enforces before this is constructed.
/// </remarks>
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
        string? productCode = null,
        string? identityKind = null,
        string? stableKey = null,
        string? versionKey = null,
        string? confidence = null,
        string? category = null,
        string? packageFamilyName = null,
        string? packageFullName = null,
        string? upgradeCode = null,
        string? executablePath = null,
        string? signerSubject = null,
        string? signatureStatus = null)
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
        IdentityKind = Guard.OptionalMaxLength(identityKind, 32);
        StableKey = Guard.OptionalMaxLength(stableKey, 1024);
        VersionKey = Guard.OptionalMaxLength(versionKey, 256);
        Confidence = Guard.OptionalMaxLength(confidence, 16);
        Category = Guard.OptionalMaxLength(category, 32);
        PackageFamilyName = Guard.OptionalMaxLength(packageFamilyName, 256);
        PackageFullName = Guard.OptionalMaxLength(packageFullName, 256);
        UpgradeCode = Guard.OptionalMaxLength(upgradeCode, 64);
        ExecutablePath = Guard.OptionalMaxLength(executablePath, 512);
        SignerSubject = Guard.OptionalMaxLength(signerSubject, 512);
        SignatureStatus = Guard.OptionalMaxLength(signatureStatus, 16);
    }

    public Guid DeviceId { get; private set; }

    public string Name { get; private set; }

    public string? Version { get; private set; }

    public string? Publisher { get; private set; }

    public string? InstallDate { get; private set; }

    public string? InstallLocation { get; private set; }

    public string? Architecture { get; private set; }

    public DateTimeOffset CollectedAt { get; private set; }

    public string? InstallationScope { get; private set; }

    public string? InstalledForUser { get; private set; }

    public string? ProductCode { get; private set; }

    /// <summary>Package, WindowsInstaller, Registered or Executable; null from older agents.</summary>
    public string? IdentityKind { get; private set; }

    /// <summary>The identity that survives an update; the key a block rule will one day name.</summary>
    public string? StableKey { get; private set; }

    public string? VersionKey { get; private set; }

    /// <summary>Installed or Observed; null from older agents, for which every row was an installation.</summary>
    public string? Confidence { get; private set; }

    public string? Category { get; private set; }

    public string? PackageFamilyName { get; private set; }

    public string? PackageFullName { get; private set; }

    public string? UpgradeCode { get; private set; }

    public string? ExecutablePath { get; private set; }

    public string? SignerSubject { get; private set; }

    public string? SignatureStatus { get; private set; }
}
