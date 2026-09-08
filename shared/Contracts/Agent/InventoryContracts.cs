namespace EndpointPlatform.Contracts.Agent;

/// <summary>
/// Request body for <c>POST /agent/v1/inventory</c>: a full snapshot of the
/// machine's hardware and network facts. Uploads replace the previous snapshot
/// wholesale — no diffing on the wire, which keeps the agent stateless about what
/// the server already knows.
/// </summary>
/// <param name="LocalAccounts">
/// Windows local users/groups/membership. Nullable: agents predating this
/// section omit it, and the server keeps whatever it last knew.
/// </param>
public sealed record InventoryReport(
    InventoryHardware Hardware,
    IReadOnlyList<InventoryNetworkInterface> NetworkInterfaces,
    string? LoggedOnUser,
    DateTimeOffset CollectedAt,
    InventoryLocalAccounts? LocalAccounts = null,
    IReadOnlyList<InventorySoftware>? Software = null,
    InventorySecurityPosture? SecurityPosture = null,
    IReadOnlyList<InventoryService>? Services = null,
    IReadOnlyList<InventoryProcess>? Processes = null,
    InventoryWindowsUpdate? WindowsUpdate = null,
    IReadOnlyList<InventoryDriver>? Drivers = null,
    InventoryBitLocker? BitLocker = null);

/// <summary>
/// BitLocker volume encryption, as reported by the endpoint.
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="Status"/> is carried separately from the volume list because an
/// empty list is ambiguous on its own: it could mean a machine with nothing
/// encryptable, or an agent that was refused the query. BitLocker's WMI provider
/// needs elevation, so the second happens, and reading it as the first would show a
/// fully encrypted estate as plaintext.
/// </para>
/// <para>
/// No recovery key appears anywhere in this contract, by construction. The agent
/// reports that a recovery-password protector exists and the GUID identifying it; it
/// never calls the method that returns the password, so there is no field here for
/// one and nothing to redact downstream.
/// </para>
/// </remarks>
/// <param name="Status">
/// "Available", "AccessDenied", "NotAvailable" or "Error". Anything unrecognised is
/// treated by the server as unknown, never as unencrypted.
/// </param>
public sealed record InventoryBitLocker(
    string Status,
    IReadOnlyList<InventoryBitLockerVolume> Volumes);

/// <summary>
/// One encryptable volume. Raw Windows values, classified server-side.
/// </summary>
/// <param name="DeviceIdentifier">The volume device id, e.g. <c>\\?\Volume{guid}\</c>.</param>
/// <param name="ConversionStatus">Win32_EncryptableVolume conversion status, null when unread.</param>
/// <param name="ProtectionStatus">Win32_EncryptableVolume protection status, null when unread.</param>
/// <param name="RecoveryProtectorIds">
/// GUIDs identifying the recovery-password protectors. Identifiers only: a protector
/// id reveals nothing and unlocks nothing.
/// </param>
/// <param name="HasTpmProtector">
/// Whether a TPM-only startup protector (type 1) exists. Null when unread. Reported
/// separately from the recovery-password protector and never merged with it: the two
/// answer different questions and only one of them has a secret behind it.
/// </param>
/// <param name="TpmProtectorIds">GUIDs identifying the TPM-only protectors.</param>
/// <param name="HasTpmPinProtector">
/// Whether a TPM+PIN startup protector (type 4) exists. Null when unread.
/// </param>
/// <param name="TpmPinProtectorIds">
/// GUIDs identifying the TPM+PIN protectors. Identifiers only. There is no field
/// here for a PIN, and the agent never asks Windows for one -- a startup PIN cannot
/// be read back from a protector at all, only replaced.
/// </param>
public sealed record InventoryBitLockerVolume(
    string DeviceIdentifier,
    string? DriveLetter,
    string? PersistentVolumeId,
    int? VolumeType,
    int? ConversionStatus,
    int? ProtectionStatus,
    int? EncryptionPercentage,
    int? EncryptionMethod,
    bool? HasRecoveryPasswordProtector,
    IReadOnlyList<string>? RecoveryProtectorIds,
    bool? HasTpmProtector = null,
    IReadOnlyList<string>? TpmProtectorIds = null,
    bool? HasTpmPinProtector = null,
    IReadOnlyList<string>? TpmPinProtectorIds = null);

/// <summary>
/// One PnP device and its bound driver.
/// </summary>
/// <remarks>
/// <para>
/// Facts only. The agent reports the raw Windows problem code and lets the server
/// classify it, so changing how a code is judged is a server change rather than a
/// fleet-wide agent rollout.
/// </para>
/// <para>
/// Every field but the identity is nullable, and null consistently means "could not
/// be read" rather than "absent" -- notably <paramref name="ProblemCode"/>, where
/// null must never be treated as the zero that means healthy.
/// </para>
/// </remarks>
/// <param name="InstanceId">PnP instance id; the devnode's stable identity.</param>
/// <param name="ProblemCode">CM_PROB_* value, 0 for none, null when unreadable.</param>
/// <param name="IsSigned">
/// Whether the bound driver package verified against a trusted catalogue. Null when
/// it could not be determined, which is reported as unknown rather than guessed.
/// </param>
public sealed record InventoryDriver(
    string InstanceId,
    string DeviceName,
    string? DeviceClass,
    string? Manufacturer,
    string? DriverProvider,
    string? DriverVersion,
    DateTimeOffset? DriverDate,
    string? InfName,
    int? ProblemCode,
    bool? IsSigned);

/// <summary>Windows Update status: recent history plus the reboot-required flag.</summary>
public sealed record InventoryWindowsUpdate(
    bool RebootRequired,
    IReadOnlyList<InventoryUpdateHistoryEntry> History);

/// <summary>One entry from the Windows Update history.</summary>
/// <param name="Title">Update title (KB / product).</param>
/// <param name="Date">When the operation ran (UTC).</param>
/// <param name="Operation">"Installation", "Uninstallation" or "Other".</param>
/// <param name="Result">"Succeeded", "SucceededWithErrors", "Failed", "Aborted" or "InProgress".</param>
public sealed record InventoryUpdateHistoryEntry(
    string Title,
    DateTimeOffset? Date,
    string Operation,
    string Result);

/// <summary>One Windows service, as reported by the agent.</summary>
public sealed record InventoryService(
    string Name,
    string DisplayName,
    string Status,
    string StartMode);

/// <summary>
/// One running process (point-in-time snapshot; the agent caps the list to the
/// top consumers). Not authoritative real-time state - it is "as of last inventory".
/// </summary>
public sealed record InventoryProcess(
    int ProcessId,
    string Name,
    long WorkingSetBytes,
    string? ExecutablePath);

/// <summary>
/// Security posture snapshot. Every field is nullable: a value the agent could not
/// read (often because it needs elevation the agent lacks) is reported as null, not
/// guessed. The server treats null as "unknown", distinct from false.
/// </summary>
public sealed record InventorySecurityPosture(
    bool? DefenderAntivirusEnabled,
    bool? DefenderRealtimeProtectionEnabled,
    int? DefenderSignatureAgeDays,
    bool? FirewallDomainEnabled,
    bool? FirewallPrivateEnabled,
    bool? FirewallPublicEnabled,
    bool? SecureBootEnabled,
    bool? TpmPresent,
    bool? TpmEnabled,
    string? TpmSpecVersion,
    string? BitLockerSystemDriveStatus,
    int? LocalAdministratorCount);

/// <summary>One application, as discovered on the endpoint.</summary>
/// <param name="Name">Display name (required).</param>
/// <param name="Version">Display version, when present.</param>
/// <param name="Publisher">Publisher, when present.</param>
/// <param name="InstallDate">Install date as reported (yyyymmdd or free text), when present.</param>
/// <param name="InstallLocation">Install path, when present.</param>
/// <param name="Architecture">
/// Which uninstall registry view the entry was found in -- <c>x64</c>, <c>x86</c>,
/// or null for a per-user entry. Deliberately NOT the binary's architecture:
/// Chrome, Edge and Brave are 64-bit yet register under WOW6432Node, so this
/// reports where Windows recorded the product, not what the product is. The
/// console labels it accordingly rather than claiming more than it knows.
/// </param>
/// <param name="InstallationScope">
/// <c>Machine</c> for an all-users install, <c>User</c> for a per-user one. Null
/// from agents older than 1.5.0, which had no notion of scope.
/// </param>
/// <param name="InstalledForUser">
/// For a per-user install, the account it belongs to (<c>DOMAIN\name</c>, or the
/// SID when the name cannot be resolved). Null for machine-wide installs, and
/// null from agents older than 1.5.0. The same product installed for two users is
/// two real installations and is reported as two entries.
/// </param>
/// <param name="ProductCode">
/// The Windows Installer product code (<c>{GUID}</c>) when the entry has one.
/// Null for non-MSI installers. Lets an installed application be matched against
/// a managed package's MsiProductCode.
/// </param>
/// <param name="IdentityKind">
/// What identifies the application, and so how stably: <c>Package</c> (a package
/// family name), <c>WindowsInstaller</c> (an upgrade or product code),
/// <c>Registered</c> (an uninstall registration), or <c>Executable</c> (only a
/// file). Null from agents older than 1.9.0.
/// </param>
/// <param name="StableKey">
/// The identity that survives an update: a package family name, an upgrade
/// code, or a composed key of publisher, name and directory. Two rows with one
/// stable key are one application; an update replaces rather than adds.
/// </param>
/// <param name="VersionKey">What changed in the last update: a package full name or a product code.</param>
/// <param name="Confidence">
/// <c>Installed</c> when an installation record vouches for the application;
/// <c>Observed</c> when only a running process does -- a portable application.
/// Nothing weaker is reported as a row. Null from agents older than 1.9.0, for
/// which every row was an installation record.
/// </param>
/// <param name="Category">
/// What kind of software: <c>Application</c>, <c>InboxApp</c>, <c>RuntimeOrSdk</c>,
/// <c>FrameworkOrResource</c>, <c>Component</c>, <c>Observed</c> or
/// <c>Transient</c>. A label for filtering, never a reason to have dropped
/// anything.
/// </param>
/// <param name="PackageFamilyName">For an MSIX/AppX package, its family name -- the identity Windows assigns.</param>
/// <param name="PackageFullName">For an MSIX/AppX package, the deployed version's full name.</param>
/// <param name="UpgradeCode">For a Windows Installer product, its upgrade code when Windows records one.</param>
/// <param name="ExecutablePath">
/// The application's primary executable when a source named one. Informational:
/// Force Stop acts on <paramref name="InstallLocation"/>.
/// </param>
/// <param name="SignerSubject">
/// The subject the primary executable's embedded signature names, or the
/// publisher a package was signed as. For a package, verified by Windows at
/// deployment; for a file, the claim the file carries.
/// </param>
/// <param name="SignatureStatus"><c>Signed</c>, <c>Unsigned</c> or <c>Unreadable</c>; see <see cref="SignatureStatuses"/>.</param>
/// <param name="Evidence">
/// Why the endpoint believes this application exists: every source that saw it,
/// bounded to <see cref="MaxEvidence"/> entries.
/// </param>
/// <remarks>
/// The trailing parameters carry defaults so that an agent predating them
/// deserializes unchanged: System.Text.Json binds records by constructor
/// parameter name, and a payload that omits them simply leaves them null. Adding
/// fields here is therefore safe; reordering or renaming the existing ones is
/// not. The <c>Max*</c> limits are what the Agent API enforces and what the
/// agent clamps to before sending, so the two cannot drift apart.
/// </remarks>
public sealed record InventorySoftware(
    string Name,
    string? Version,
    string? Publisher,
    string? InstallDate,
    string? InstallLocation,
    string? Architecture,
    string? InstallationScope = null,
    string? InstalledForUser = null,
    string? ProductCode = null,
    string? IdentityKind = null,
    string? StableKey = null,
    string? VersionKey = null,
    string? Confidence = null,
    string? Category = null,
    string? PackageFamilyName = null,
    string? PackageFullName = null,
    string? UpgradeCode = null,
    string? ExecutablePath = null,
    string? SignerSubject = null,
    string? SignatureStatus = null,
    IReadOnlyList<InventorySoftwareEvidence>? Evidence = null)
{
    public const int MaxIdentityKind = 32;
    public const int MaxStableKey = 1024;
    public const int MaxVersionKey = 256;
    public const int MaxConfidence = 16;
    public const int MaxCategory = 32;
    public const int MaxPackageName = 256;
    public const int MaxUpgradeCode = 64;
    public const int MaxExecutablePath = 512;
    public const int MaxSignerSubject = 512;
    public const int MaxSignatureStatus = 16;

    /// <summary>The most evidence entries one row carries.</summary>
    public const int MaxEvidence = 32;

    /// <summary>The values <see cref="IdentityKind"/> may take.</summary>
    public static readonly IReadOnlySet<string> IdentityKinds =
        new HashSet<string>(StringComparer.Ordinal) { "Package", "WindowsInstaller", "Registered", "Executable" };

    /// <summary>The values <see cref="Confidence"/> may take. Weaker confidences are never rows.</summary>
    public static readonly IReadOnlySet<string> Confidences =
        new HashSet<string>(StringComparer.Ordinal) { "Installed", "Observed" };

    /// <summary>The values <see cref="Category"/> may take.</summary>
    public static readonly IReadOnlySet<string> Categories = new HashSet<string>(StringComparer.Ordinal)
    {
        "Application", "InboxApp", "RuntimeOrSdk", "FrameworkOrResource", "Component", "Observed", "Transient",
    };

    /// <summary>The values <see cref="SignatureStatus"/> may take.</summary>
    public static readonly IReadOnlySet<string> SignatureStatuses =
        new HashSet<string>(StringComparer.Ordinal) { "Signed", "Unsigned", "Unreadable" };
}

/// <summary>
/// One reason the endpoint believes an application exists: which source saw it,
/// what that source called it, and what it pointed at.
/// </summary>
/// <param name="Source">
/// <c>WindowsInstaller</c>, <c>UninstallRegistry</c>, <c>PackageRegistration</c>,
/// <c>AppPaths</c>, <c>StartMenuShortcut</c>, <c>ExecutableMetadata</c> or
/// <c>RunningProcess</c>. The first three are installation records; the rest are
/// hints that sharpen identity and location but never make anything installed.
/// </param>
/// <param name="Name">What the source called it: a shortcut's name, a file's product name, a process name.</param>
/// <param name="Detail">What it pointed at: a product code, a package full name, or an executable path.</param>
public sealed record InventorySoftwareEvidence(string Source, string? Name, string? Detail)
{
    public const int MaxSource = 32;
    public const int MaxName = 384;
    public const int MaxDetail = 512;

    /// <summary>The values <see cref="Source"/> may take.</summary>
    public static readonly IReadOnlySet<string> Sources = new HashSet<string>(StringComparer.Ordinal)
    {
        "WindowsInstaller", "UninstallRegistry", "PackageRegistration",
        "AppPaths", "StartMenuShortcut", "ExecutableMetadata", "RunningProcess",
    };
}

/// <summary>Windows local accounts snapshot.</summary>
public sealed record InventoryLocalAccounts(
    IReadOnlyList<InventoryLocalUser> Users,
    IReadOnlyList<InventoryLocalGroup> Groups);

/// <summary>
/// One local user. The SID is the stable identity — names are renameable.
/// No credential material of any kind is collected or carried.
/// </summary>
public sealed record InventoryLocalUser(
    string Sid,
    string Name,
    string? FullName,
    string? Description,
    bool Enabled,
    bool PasswordRequired,
    bool PasswordExpires,
    DateTimeOffset? LastLogon,
    bool IsLocalAdministrator);

/// <summary>One local group with its member account names/SIDs.</summary>
public sealed record InventoryLocalGroup(
    string Sid,
    string Name,
    string? Description,
    IReadOnlyList<InventoryGroupMember> Members);

/// <param name="Sid">Null for members whose SID cannot be resolved (orphaned domain members).</param>
public sealed record InventoryGroupMember(string Name, string? Sid, string MemberType);

/// <summary>Hardware section of an inventory report. Unknown values are null, never guessed.</summary>
public sealed record InventoryHardware(
    string? SerialNumber,
    string? Manufacturer,
    string? Model,
    string? CpuName,
    int? CpuPhysicalCores,
    int? CpuLogicalProcessors,
    long? TotalMemoryBytes,
    IReadOnlyList<InventoryDisk> Disks);

/// <summary>One fixed logical volume.</summary>
public sealed record InventoryDisk(
    string Name,
    string? FileSystem,
    long SizeBytes,
    long FreeBytes);

/// <summary>One network adapter.</summary>
public sealed record InventoryNetworkInterface(
    string Name,
    string? MacAddress,
    IReadOnlyList<string> IpAddresses,
    bool IsUp);

/// <summary>Response body for a successful inventory upload.</summary>
public sealed record InventoryResponse(DateTimeOffset ServerTime);
