using System.Text.Json;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Drivers;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Software;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Drivers;

public enum DriverPackageCreateStatus
{
    Created = 0,
    Duplicate = 1,
    HashMismatch = 2,
    Invalid = 3,
}

public sealed record DriverPackageCreateResult(
    DriverPackageCreateStatus Status, DriverPackage? Package, string? Error)
{
    public static DriverPackageCreateResult Created(DriverPackage package) =>
        new(DriverPackageCreateStatus.Created, package, null);

    public static DriverPackageCreateResult Duplicate() =>
        new(DriverPackageCreateStatus.Duplicate, null, "A driver package with this content already exists.");

    public static DriverPackageCreateResult HashMismatch() =>
        new(DriverPackageCreateStatus.HashMismatch, null,
            "The uploaded content does not match the declared SHA-256.");

    public static DriverPackageCreateResult Invalid(string error) =>
        new(DriverPackageCreateStatus.Invalid, null, error);
}

public enum DriverDeployStatus
{
    Queued = 0,
    PackageNotFound = 1,
    DeviceNotFound = 2,
    AgentTooOld = 3,
}

public sealed record DriverDeployResult(DriverDeployStatus Status, DeviceTask? Task, string? Error);

/// <summary>
/// The approved driver-package catalogue, and deployment of a package to a device.
/// </summary>
/// <remarks>
/// <para>
/// Modelled directly on <see cref="SoftwarePackageService"/> and sharing its content
/// store: bytes are written first, hash-verified by the store itself, and the
/// metadata row only afterwards, so a committed row always has its content.
/// </para>
/// <para>
/// This service decides nothing about trust. It records what an administrator
/// approved and hands the pins to the endpoint, which verifies all of them itself
/// before Windows sees a single file. A compromised server cannot get an unsigned
/// driver installed by lying here.
/// </para>
/// </remarks>
public sealed class DriverPackageService(
    EndpointPlatformDbContext dbContext,
    IPackageContentStore contentStore,
    DeviceTaskService taskService,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<DriverPackageService> logger)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly IPackageContentStore _contentStore = contentStore
        ?? throw new ArgumentNullException(nameof(contentStore));

    private readonly DeviceTaskService _taskService = taskService
        ?? throw new ArgumentNullException(nameof(taskService));

    private readonly AuditWriter _auditWriter = auditWriter
        ?? throw new ArgumentNullException(nameof(auditWriter));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<DriverPackageService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public async Task<DriverPackageCreateResult> CreateAsync(
        Guid organizationId,
        string name,
        string version,
        string? provider,
        string declaredSha256,
        string fileName,
        string infFileName,
        string hardwareId,
        string? driverVersion,
        string requiredSignerSubject,
        Stream content,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var normalizedHash = (declaredSha256 ?? "").Trim().ToLowerInvariant();

        var duplicate = await _dbContext.DriverPackages.AnyAsync(
            p => p.OrganizationId == organizationId && p.Sha256 == normalizedHash, cancellationToken);
        if (duplicate)
        {
            return DriverPackageCreateResult.Duplicate();
        }

        long sizeBytes;
        try
        {
            sizeBytes = await _contentStore.SaveAsync(normalizedHash, content, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return DriverPackageCreateResult.HashMismatch();
        }

        DriverPackage package;
        try
        {
            package = new DriverPackage(
                organizationId, name, version, provider, normalizedHash, fileName, sizeBytes,
                infFileName, hardwareId, driverVersion, requiredSignerSubject, actorId, actorDisplay);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return DriverPackageCreateResult.Invalid(ex.Message);
        }

        _dbContext.DriverPackages.Add(package);

        // Approving a driver package is the decision that matters: it is the moment
        // somebody says this kernel code may run on the estate. The install itself is
        // already covered by task.queue/task.result, so this is the one event the
        // task pipeline does not already record.
        _auditWriter.Stage(
            organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "driver.package.created", AuditResult.Success,
            a => a.OnTarget("driver_package", package.Id.ToString(), $"{package.Name} {package.Version}")
                .Requiring(Permissions.Driver.Manage)
                .WithStateChange(null, JsonSerializer.Serialize(new
                {
                    sha256 = package.Sha256,
                    fileName = package.FileName,
                    infFileName = package.InfFileName,
                    hardwareId = package.HardwareId,
                    driverVersion = package.DriverVersion,
                    requiredSigner = package.RequiredSignerSubject,
                    sizeBytes = package.SizeBytes,
                })));

        await _dbContext.SaveChangesAsync(cancellationToken);
        return DriverPackageCreateResult.Created(package);
    }

    public async Task<IReadOnlyList<DriverPackage>> ListAsync(
        Guid organizationId, CancellationToken cancellationToken = default) =>
        await _dbContext.DriverPackages
            .AsNoTracking()
            .Where(p => p.OrganizationId == organizationId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<DriverPackage?> GetDeployableAsync(
        Guid organizationId, Guid packageId, CancellationToken cancellationToken = default) =>
        await _dbContext.DriverPackages
            .AsNoTracking()
            .SingleOrDefaultAsync(
                p => p.Id == packageId && p.OrganizationId == organizationId && !p.IsWithdrawn,
                cancellationToken);

    public async Task<bool> WithdrawAsync(
        Guid organizationId, Guid packageId, Guid actorId, string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var package = await _dbContext.DriverPackages
            .SingleOrDefaultAsync(p => p.Id == packageId && p.OrganizationId == organizationId, cancellationToken);

        if (package is null)
        {
            return false;
        }

        if (!package.Withdraw(_timeProvider.GetUtcNow()))
        {
            return true;
        }

        _auditWriter.Stage(
            organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "driver.package.withdrawn", AuditResult.Success,
            a => a.OnTarget("driver_package", package.Id.ToString(), $"{package.Name} {package.Version}")
                .Requiring(Permissions.Driver.Manage)
                .WithStateChange(
                    JsonSerializer.Serialize(new { withdrawn = false }),
                    JsonSerializer.Serialize(new { withdrawn = true })));

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Queues an installation of one approved package on one device.
    /// </summary>
    /// <remarks>
    /// The agent-version check happens here as well as inside
    /// <see cref="DeviceTaskService"/> so the caller can be told which of the two
    /// refusals it hit -- an unsupported agent and a missing device are different
    /// problems with different remedies, and collapsing both into "not found" would
    /// send an operator hunting for a device that is sitting right there.
    /// </remarks>
    public async Task<DriverDeployResult> DeployAsync(
        Guid organizationId,
        Guid packageId,
        Guid deviceId,
        bool allowDowngrade,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var package = await GetDeployableAsync(organizationId, packageId, cancellationToken);
        if (package is null)
        {
            return new DriverDeployResult(DriverDeployStatus.PackageNotFound, null,
                "No approved driver package with that id.");
        }

        var device = await _dbContext.Devices
            .AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == deviceId && d.OrganizationId == organizationId, cancellationToken);

        if (device is null)
        {
            return new DriverDeployResult(DriverDeployStatus.DeviceNotFound, null, "No such device.");
        }

        var definition = DeviceTaskCatalog.Require(DeviceTaskType.InstallDriverPackage);
        if (!DeviceTaskCatalog.IsSupportedBy(definition, device.AgentVersion))
        {
            return new DriverDeployResult(DriverDeployStatus.AgentTooOld, null,
                $"This device runs agent {device.AgentVersion}; driver installation needs "
                + $"{definition.MinimumAgentVersion} or newer. Update the agent first.");
        }

        var payload = new TaskPayloads.InstallDriverPackage(
            package.Id,
            package.Sha256,
            package.InfFileName,
            package.HardwareId,
            package.RequiredSignerSubject,
            package.Provider,
            package.DriverVersion,
            allowDowngrade,
            $"{package.Name} {package.Version}",
            _timeProvider.GetUtcNow());

        var task = await _taskService.QueueAsync(
            organizationId, deviceId, DeviceTaskType.InstallDriverPackage, payload,
            actorId, actorDisplay, cancellationToken);

        if (task is null)
        {
            return new DriverDeployResult(DriverDeployStatus.DeviceNotFound, null,
                "The device could not accept the task.");
        }

        _logger.LogInformation(
            "Queued driver package {Package} for device {DeviceId} (downgrade allowed: {Downgrade}).",
            package.Id, deviceId, allowDowngrade);

        return new DriverDeployResult(DriverDeployStatus.Queued, task, null);
    }
}
