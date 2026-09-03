using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Software;

/// <summary>How a deployment's targets were chosen.</summary>
public enum DeploymentTargetType
{
    Devices = 0,
    Groups = 1,
    Mixed = 2,
}

/// <summary>
/// One administrator's decision to put a package onto a set of devices.
/// </summary>
/// <remarks>
/// <para>
/// Durable in PostgreSQL, because it is the record of what was intended. The
/// tasks it creates are the record of what happened, and they expire; without
/// this row a deployment would exist only as scattered tasks with no way to
/// answer who ordered it, what was targeted, or which devices were deliberately
/// skipped and why.
/// </para>
/// <para>
/// Deliberately holds no aggregate status column. Progress is derived by reading
/// the targets and their tasks, so the console cannot show a stored status that
/// has drifted from the tasks it claims to summarise -- there is one source of
/// truth for execution and it is the task.
/// </para>
/// </remarks>
public sealed class SoftwareDeployment : AuditableEntity
{
    private SoftwareDeployment()
    {
        CreatedByDisplay = null!;
        PackageName = null!;
        PackageVersion = null!;
    }

    public SoftwareDeployment(
        Guid organizationId,
        Guid packageId,
        string packageName,
        string packageVersion,
        DeploymentTargetType targetType,
        Guid createdByUserId,
        string createdByDisplay)
    {
        OrganizationId = Guard.NotEmpty(organizationId);
        PackageId = Guard.NotEmpty(packageId);
        // Copied, not joined. A package can be withdrawn or its metadata edited;
        // the deployment must still say what was actually sent, months later.
        PackageName = Guard.NotNullOrWhiteSpace(packageName, nameof(packageName), maxLength: 256);
        PackageVersion = Guard.NotNullOrWhiteSpace(packageVersion, nameof(packageVersion), maxLength: 128);
        TargetType = targetType;
        CreatedByUserId = Guard.NotEmpty(createdByUserId);
        CreatedByDisplay = Guard.NotNullOrWhiteSpace(createdByDisplay, nameof(createdByDisplay), maxLength: 256);
    }

    public Guid OrganizationId { get; private set; }

    public Guid PackageId { get; private set; }

    public string PackageName { get; private set; }

    public string PackageVersion { get; private set; }

    public DeploymentTargetType TargetType { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public string CreatedByDisplay { get; private set; }
}

/// <summary>What a deployment decided for one device.</summary>
/// <remarks>
/// Only two values, and both are decisions rather than progress. Execution
/// status lives on the linked task, so this never has to be kept in step with
/// it: a target is either something the deployment queued, or something it
/// deliberately did not.
/// </remarks>
public enum DeploymentTargetState
{
    /// <summary>An InstallPackage task was created. Progress is the task's.</summary>
    Queued = 0,

    /// <summary>No task was created. <see cref="SoftwareDeploymentTarget.Reason"/> says why.</summary>
    Skipped = 1,
}

/// <summary>
/// One device in a deployment: what was decided for it, and the task if any.
/// </summary>
/// <remarks>
/// A skipped device is recorded as deliberately as a queued one. "Nothing was
/// sent to this machine" is the answer to most questions an administrator asks
/// after a deployment, and a row that only listed successes could not give it.
/// </remarks>
public sealed class SoftwareDeploymentTarget : AuditableEntity
{
    private SoftwareDeploymentTarget()
    {
    }

    public SoftwareDeploymentTarget(
        Guid deploymentId,
        Guid deviceId,
        DeploymentTargetState state,
        SoftwareEligibility reason,
        Guid? taskId,
        string? observedVersion)
    {
        DeploymentId = Guard.NotEmpty(deploymentId);
        DeviceId = Guard.NotEmpty(deviceId);
        State = state;
        Reason = reason;
        TaskId = taskId;
        ObservedVersion = Guard.OptionalMaxLength(observedVersion, 128);
    }

    public Guid DeploymentId { get; private set; }

    public Guid DeviceId { get; private set; }

    public DeploymentTargetState State { get; private set; }

    /// <summary>The eligibility verdict, kept for both queued and skipped targets.</summary>
    public SoftwareEligibility Reason { get; private set; }

    /// <summary>The InstallPackage task, when one was created.</summary>
    public Guid? TaskId { get; private set; }

    /// <summary>
    /// The version observed on the device when the decision was made.
    /// </summary>
    /// <remarks>
    /// A snapshot, so "skipped: already installed" can be justified after the
    /// fact even once inventory has moved on.
    /// </remarks>
    public string? ObservedVersion { get; private set; }
}
