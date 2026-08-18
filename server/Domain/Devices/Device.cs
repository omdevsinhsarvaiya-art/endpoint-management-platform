using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// A managed Windows endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Created exclusively by successful enrollment — there is no "add device" form,
/// because a device row without a credentialled agent behind it would be
/// indistinguishable from a dead one.
/// </para>
/// <para>
/// Online/offline is deliberately NOT a stored flag. It is derived from
/// <see cref="LastSeenAt"/> versus the heartbeat interval at query time, so a
/// crashed agent cannot leave a device marked "online" forever, and the
/// definition of "online" can be tuned without a data migration.
/// </para>
/// </remarks>
public sealed class Device : AuditableEntity
{
    private Device()
    {
        Hostname = null!;
        MachineIdentifier = null!;
        AgentVersion = null!;
    }

    public Device(
        Guid organizationId,
        string hostname,
        string machineIdentifier,
        string agentVersion,
        string? operatingSystem)
    {
        OrganizationId = Guard.NotEmpty(organizationId);
        Hostname = Guard.NotNullOrWhiteSpace(hostname, nameof(hostname), maxLength: 253);
        MachineIdentifier = Guard.NotNullOrWhiteSpace(machineIdentifier, nameof(machineIdentifier), maxLength: 128);
        AgentVersion = Guard.NotNullOrWhiteSpace(agentVersion, nameof(agentVersion), maxLength: 64);
        OperatingSystem = Guard.OptionalMaxLength(operatingSystem, 256);
        Status = DeviceStatus.Active;
    }

    public Guid OrganizationId { get; private set; }

    /// <summary>Hostname as reported by the agent. Display data, not identity.</summary>
    public string Hostname { get; private set; }

    /// <summary>
    /// Stable machine identifier (SMBIOS UUID where available) used to detect
    /// re-enrollment of a known machine. NOT a secret and NOT authentication —
    /// it is spoofable by design and treated purely as a dedup hint.
    /// </summary>
    public string MachineIdentifier { get; private set; }

    public string AgentVersion { get; private set; }

    public string? OperatingSystem { get; private set; }

    public DeviceStatus Status { get; private set; }

    /// <summary>Set on enrollment and on every authenticated heartbeat.</summary>
    public DateTimeOffset? LastSeenAt { get; private set; }

    /// <summary>Interactive user reported by the last inventory, e.g. <c>DOMAIN\jsmith</c>.</summary>
    public string? LoggedOnUser { get; private set; }

    /// <summary>
    /// When an administrator last asked for a fresh inventory. The next heartbeat
    /// response tells the agent to upload one; comparing against
    /// <see cref="InventoryCollectedAt"/> decides whether the request is still
    /// outstanding. Pull-based on purpose: the server never connects to agents.
    /// </summary>
    public DateTimeOffset? InventoryRequestedAt { get; private set; }

    /// <summary>Server receive time of the most recent inventory upload.</summary>
    public DateTimeOffset? InventoryCollectedAt { get; private set; }

    /// <summary>The enrollment token that admitted this device, for audit lineage.</summary>
    public Guid EnrolledWithTokenId { get; private set; }

    public DateTimeOffset EnrolledAt { get; private set; }

    public bool IsRetired => Status == DeviceStatus.Retired;

    public static Device Enroll(
        Guid organizationId,
        string hostname,
        string machineIdentifier,
        string agentVersion,
        string? operatingSystem,
        Guid enrollmentTokenId,
        DateTimeOffset now)
    {
        var device = new Device(organizationId, hostname, machineIdentifier, agentVersion, operatingSystem)
        {
            EnrolledWithTokenId = Guard.NotEmpty(enrollmentTokenId),
            EnrolledAt = now,
            LastSeenAt = now,
        };

        return device;
    }

    /// <summary>Applies an authenticated heartbeat.</summary>
    public void RecordHeartbeat(string hostname, string agentVersion, string? operatingSystem, DateTimeOffset now)
    {
        // A retired device's heartbeats are rejected upstream; guard here too so a
        // coding error cannot quietly resurrect one.
        if (IsRetired)
        {
            throw new InvalidOperationException(
                $"Device {Id} is retired and cannot record heartbeats.");
        }

        Hostname = Guard.NotNullOrWhiteSpace(hostname, nameof(hostname), maxLength: 253);
        AgentVersion = Guard.NotNullOrWhiteSpace(agentVersion, nameof(agentVersion), maxLength: 64);

        if (operatingSystem is not null)
        {
            OperatingSystem = Guard.OptionalMaxLength(operatingSystem, 256);
        }

        LastSeenAt = now;
    }

    /// <summary>
    /// Re-enrollment of a machine that already has a device record (e.g. after an
    /// OS reinstall): refresh the reported facts and the admitting token, keep the
    /// device id and its history.
    /// </summary>
    public void ReEnroll(
        string hostname,
        string agentVersion,
        string? operatingSystem,
        Guid enrollmentTokenId,
        DateTimeOffset now)
    {
        if (IsRetired)
        {
            throw new InvalidOperationException(
                $"Device {Id} is retired; re-enrollment requires an administrator to reactivate it first.");
        }

        Hostname = Guard.NotNullOrWhiteSpace(hostname, nameof(hostname), maxLength: 253);
        AgentVersion = Guard.NotNullOrWhiteSpace(agentVersion, nameof(agentVersion), maxLength: 64);
        OperatingSystem = Guard.OptionalMaxLength(operatingSystem, 256);
        EnrolledWithTokenId = Guard.NotEmpty(enrollmentTokenId);
        EnrolledAt = now;
        LastSeenAt = now;
    }

    /// <summary>Marks an administrator's request for a fresh inventory.</summary>
    public void RequestInventoryRefresh(DateTimeOffset now) => InventoryRequestedAt = now;

    /// <summary>True when the agent should be told to upload inventory.</summary>
    public bool IsInventoryRefreshPending =>
        InventoryCollectedAt is null
        || (InventoryRequestedAt is { } requested && requested > InventoryCollectedAt);

    /// <summary>Applies the device-level facts carried by an inventory upload.</summary>
    public void RecordInventory(string? loggedOnUser, DateTimeOffset now)
    {
        if (IsRetired)
        {
            throw new InvalidOperationException($"Device {Id} is retired and cannot record inventory.");
        }

        LoggedOnUser = Guard.OptionalMaxLength(loggedOnUser, 256);
        InventoryCollectedAt = now;
    }

    public void Retire() => Status = DeviceStatus.Retired;

    /// <summary>
    /// Returns a retired device to service so it can enroll again. Offboarding
    /// revokes the device's credentials; reactivation does not restore them - the
    /// machine must re-enroll to obtain a fresh credential.
    /// </summary>
    public void Reactivate() => Status = DeviceStatus.Active;

    /// <summary>Whether the device counts as online given the staleness threshold.</summary>
    public bool IsOnline(DateTimeOffset now, TimeSpan staleAfter) =>
        Status == DeviceStatus.Active
        && LastSeenAt is { } lastSeen
        && now - lastSeen <= staleAfter;
}
