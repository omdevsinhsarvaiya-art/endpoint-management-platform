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
    IReadOnlyList<string>? RecoveryProtectorIds);

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

/// <summary>One installed application, read from the Windows uninstall registry.</summary>
/// <param name="Name">Display name (required).</param>
/// <param name="Version">Display version, when present.</param>
/// <param name="Publisher">Publisher, when present.</param>
/// <param name="InstallDate">Install date as reported (yyyymmdd or free text), when present.</param>
/// <param name="InstallLocation">Install path, when present.</param>
/// <param name="Architecture">"x64", "x86" or null.</param>
public sealed record InventorySoftware(
    string Name,
    string? Version,
    string? Publisher,
    string? InstallDate,
    string? InstallLocation,
    string? Architecture);

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
