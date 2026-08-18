using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Devices;

/// <summary>
/// Device offboarding and reactivation - the end of a device's managed life and,
/// if needed, its return.
/// </summary>
/// <remarks>
/// <para>
/// Offboarding is deliberately a <b>logical</b> operation: it revokes every active
/// credential and marks the device retired, so the machine can no longer heartbeat,
/// upload inventory, receive tasks, or re-enroll on the old credential. It does
/// <b>not</b> perform a destructive remote wipe. A wipe is irreversible and would
/// require its own guarded, explicitly-confirmed, agent-side executor; it is out of
/// scope here by design, not by omission. Offboarding here is fully reversible via
/// <see cref="ReactivateAsync"/> (the machine then re-enrolls for a fresh credential).
/// </para>
/// <para>
/// Both operations are audited under <see cref="Permissions.Device.Retire"/>.
/// </para>
/// </remarks>
public sealed class DeviceLifecycleService(
    EndpointPlatformDbContext dbContext,
    AuditWriter auditWriter,
    TimeProvider timeProvider)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly AuditWriter _auditWriter = auditWriter;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>
    /// Revokes the device's active credentials and retires it. Idempotent: a device
    /// already retired is reported as success without further change.
    /// </summary>
    public async Task<DeviceLifecycleResult> OffboardAsync(
        Guid organizationId, Guid deviceId, Guid actorId, string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var device = await _dbContext.Devices.SingleOrDefaultAsync(
            d => d.Id == deviceId && d.OrganizationId == organizationId, cancellationToken);
        if (device is null)
        {
            return DeviceLifecycleResult.NotFound;
        }

        var now = _timeProvider.GetUtcNow();

        var activeCredentials = await _dbContext.AgentCredentials
            .Where(c => c.DeviceId == deviceId && c.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var credential in activeCredentials)
        {
            credential.Revoke(now);
        }

        var wasRetired = device.IsRetired;
        device.Retire();

        var before = System.Text.Json.JsonSerializer.Serialize(new { status = wasRetired ? "Retired" : "Active" });
        var after = System.Text.Json.JsonSerializer.Serialize(new
        {
            status = "Retired",
            revokedCredentials = activeCredentials.Count,
        });

        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "device.offboard", AuditResult.Success,
            a => a.OnDevice(device.Id, device.Hostname)
                  .Requiring(Permissions.Device.Retire)
                  .WithStateChange(before, after));

        await _dbContext.SaveChangesAsync(cancellationToken);
        return DeviceLifecycleResult.Success;
    }

    /// <summary>Returns a retired device to service. The machine must re-enroll for a new credential.</summary>
    public async Task<DeviceLifecycleResult> ReactivateAsync(
        Guid organizationId, Guid deviceId, Guid actorId, string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var device = await _dbContext.Devices.SingleOrDefaultAsync(
            d => d.Id == deviceId && d.OrganizationId == organizationId, cancellationToken);
        if (device is null)
        {
            return DeviceLifecycleResult.NotFound;
        }

        if (!device.IsRetired)
        {
            return DeviceLifecycleResult.Success; // Already active.
        }

        device.Reactivate();

        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "device.reactivate", AuditResult.Success,
            a => a.OnDevice(device.Id, device.Hostname)
                  .Requiring(Permissions.Device.Retire)
                  .WithStateChange("""{"status":"Retired"}""", """{"status":"Active"}"""));

        await _dbContext.SaveChangesAsync(cancellationToken);
        return DeviceLifecycleResult.Success;
    }
}

public enum DeviceLifecycleResult
{
    Success = 0,
    NotFound = 1,
}
