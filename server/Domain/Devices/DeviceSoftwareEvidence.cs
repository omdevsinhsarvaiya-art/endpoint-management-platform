using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// One reason a device believes an application exists: which discovery source
/// saw it, what that source called it, and what it pointed at.
/// </summary>
/// <remarks>
/// Stored so "why does Techsara believe this application is installed?" has an
/// answer that is data rather than inference, and so a wrong merge on the
/// endpoint is visible in the console rather than silent. Rows belong to one
/// <see cref="DeviceSoftware"/> row and are replaced with it on every inventory.
/// </remarks>
public sealed class DeviceSoftwareEvidence : AuditableEntity
{
    /// <summary>
    /// The source that means "a process of this application was running when
    /// inventory was collected". It is the only running signal the server holds,
    /// and it is a snapshot: absent evidence of any kind means an agent too old
    /// to report it (before 1.9.0), not an application that is stopped.
    /// </summary>
    public const string RunningProcessSource = "RunningProcess";

    private DeviceSoftwareEvidence()
    {
        Source = null!;
    }

    public DeviceSoftwareEvidence(Guid deviceSoftwareId, int ordinal, string source, string? name, string? detail)
    {
        DeviceSoftwareId = Guard.NotEmpty(deviceSoftwareId);
        Ordinal = ordinal;
        Source = Guard.NotNullOrWhiteSpace(source, nameof(source), maxLength: 32);
        Name = Guard.OptionalMaxLength(name, 384);
        Detail = Guard.OptionalMaxLength(detail, 512);
    }

    public Guid DeviceSoftwareId { get; private set; }

    /// <summary>The order the endpoint reported it in: installation records first, then what attached to them.</summary>
    public int Ordinal { get; private set; }

    public string Source { get; private set; }

    public string? Name { get; private set; }

    public string? Detail { get; private set; }
}
