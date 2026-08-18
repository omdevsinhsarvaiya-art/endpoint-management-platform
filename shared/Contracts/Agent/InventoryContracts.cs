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
    InventoryWindowsUpdate? WindowsUpdate = null);

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
