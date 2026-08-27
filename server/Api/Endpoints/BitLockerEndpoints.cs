using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// BitLocker volume inventory and encryption readiness.
/// </summary>
/// <remarks>
/// <para>
/// Read-only in this milestone. Both routes sit behind <c>bitlocker.view</c> and the
/// device scope check, and there is deliberately no route here that encrypts,
/// decrypts, suspends or resumes anything.
/// </para>
/// <para>
/// <b>No recovery key can be returned by these endpoints</b>, and not because they
/// filter one out: the agent never reads a recovery key, so none exists in the
/// database, the contracts, or the projections below. What is returned is that a
/// recovery-password protector exists and the GUID identifying it.
/// </para>
/// </remarks>
public static class BitLockerEndpoints
{
    public static IEndpointRouteBuilder MapBitLockerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/devices/{deviceId:guid}");

        group.MapGet("/bitlocker-volumes", ListVolumesAsync)
            .WithName("ListDeviceBitLockerVolumes")
            .RequirePermission(Permissions.BitLocker.View);

        group.MapGet("/bitlocker-readiness", GetReadinessAsync)
            .WithName("GetDeviceBitLockerReadiness")
            .RequirePermission(Permissions.BitLocker.View);

        return endpoints;
    }

    private static async Task<IResult> ListVolumesAsync(
        Guid deviceId,
        EndpointPlatformDbContext dbContext,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return OutOfScope();
        }

        var rows = await dbContext.DeviceBitLockerVolumes
            .AsNoTracking()
            .Where(v => v.DeviceId == deviceId)
            .OrderBy(v => v.DriveLetter)
            .ToListAsync(cancellationToken);

        return Results.Ok(rows.Select(v => new
        {
            deviceIdentifier = v.DeviceIdentifier,
            driveLetter = v.DriveLetter,
            persistentVolumeId = v.PersistentVolumeId,
            volumeType = v.VolumeType,

            // Both the raw Windows values and this platform's reading of them. The
            // raw pair is what an engineer will check; the state is what the console
            // acts on, and conflating them would hide the disagreement when one of
            // them is unreadable.
            conversionStatus = v.ConversionStatus,
            protectionStatus = v.ProtectionStatus,
            state = BitLockerPosture.ClassifyVolume(v.ConversionStatus, v.ProtectionStatus).ToString(),

            encryptionPercentage = v.EncryptionPercentage,
            encryptionMethod = v.EncryptionMethod,

            // Presence and identity of the recovery protector. Never the key: the
            // agent does not read one, so there is nothing here to withhold.
            hasRecoveryPasswordProtector = v.HasRecoveryPasswordProtector,
            recoveryProtectorIds = string.IsNullOrEmpty(v.RecoveryProtectorIds)
                ? Array.Empty<string>()
                : v.RecoveryProtectorIds.Split(','),

            collectedAt = v.CollectedAt,
        }));
    }

    private static async Task<IResult> GetReadinessAsync(
        Guid deviceId,
        EndpointPlatformDbContext dbContext,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return OutOfScope();
        }

        var device = await dbContext.Devices
            .AsNoTracking()
            .Where(d => d.Id == deviceId && d.OrganizationId == actor.OrganizationId)
            .Select(d => new { d.Id, d.Hostname, d.DisplayName })
            .SingleOrDefaultAsync(cancellationToken);

        if (device is null)
        {
            return Results.NotFound();
        }

        var status = await dbContext.DeviceBitLockerStatus
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.DeviceId == deviceId, cancellationToken);

        var volumes = await dbContext.DeviceBitLockerVolumes
            .AsNoTracking()
            .Where(v => v.DeviceId == deviceId)
            .ToListAsync(cancellationToken);

        // TPM state comes from the security posture the agent already reports, read
        // here rather than duplicated: readiness is an encryption question and a TPM
        // question, and the TPM half already has an owner.
        var posture = await dbContext.DeviceSecurityPosture
            .AsNoTracking()
            .Where(p => p.DeviceId == deviceId)
            .Select(p => new { p.TpmPresent, p.TpmEnabled, p.TpmSpecVersion, p.BitLockerSystemDriveStatus })
            .SingleOrDefaultAsync(cancellationToken);

        var result = BitLockerPosture.Evaluate(
            status?.Availability ?? BitLockerAvailability.Unknown,
            volumes.Select(v => v.ToView()).ToList(),
            posture?.TpmPresent,
            posture?.TpmEnabled);

        return Results.Ok(new
        {
            deviceId = device.Id,
            hostname = device.Hostname,
            displayName = device.DisplayName,

            readiness = result.Readiness.ToString(),

            // Carried alongside the verdict so a reader can tell a machine that is
            // unencrypted from one that would not answer.
            availability = result.Availability.ToString(),

            lastReportedAt = status?.CollectedAt,

            tpmPresent = posture?.TpmPresent,
            tpmEnabled = posture?.TpmEnabled,
            tpmSpecVersion = posture?.TpmSpecVersion,

            // The long-standing single-field summary, unchanged in meaning, so a
            // caller comparing this against the compliance score sees the same value
            // the score was computed from.
            systemDriveStatus = posture?.BitLockerSystemDriveStatus,

            protectedVolumeCount = result.ProtectedVolumeCount,
            unprotectedVolumeCount = result.UnprotectedVolumeCount,
            unknownVolumeCount = result.UnknownVolumeCount,
            totalVolumeCount = result.Volumes.Count,

            volumes = result.Volumes.Select(v => new
            {
                deviceIdentifier = v.DeviceIdentifier,
                driveLetter = v.DriveLetter,
                isOperatingSystemVolume = v.IsOperatingSystemVolume,
                state = v.State.ToString(),
                hasRecoveryPasswordProtector = v.HasRecoveryPasswordProtector,
            }),

            limitation =
                "Encryption state is read from Win32_EncryptableVolume at the last inventory and needs an "
                + "elevated agent; a volume the endpoint could not read is reported Unknown, never as "
                + "unencrypted. Whether a recovery password has been escrowed to a directory is not "
                + "determined, and recovery keys are never collected.",
        });
    }

    /// <summary>
    /// Deliberately 404, matching the elevation and driver endpoints: an
    /// administrator who cannot reach a device should not learn that it exists.
    /// </summary>
    private static IResult OutOfScope() => Results.NotFound();
}
