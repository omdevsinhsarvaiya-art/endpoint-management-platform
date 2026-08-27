namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// One device's driver, reduced to what a health verdict needs.
/// </summary>
/// <remarks>
/// A view rather than the entity, so the rollup can be evaluated over reported
/// facts that have not been persisted yet -- which is exactly what the inventory
/// service needs in order to compare the incoming snapshot against the stored one.
/// </remarks>
/// <param name="InstanceId">The PnP instance id: the stable identity of the devnode.</param>
public sealed record DriverView(
    string InstanceId,
    string DeviceName,
    string? DeviceClass,
    int? ProblemCode);

/// <summary>One device driver, with its verdict attached.</summary>
public sealed record DriverFinding(
    string InstanceId,
    string DeviceName,
    string? DeviceClass,
    DriverHealthVerdict Verdict);

/// <summary>
/// The driver health of one endpoint.
/// </summary>
/// <param name="OverallState">
/// The worst thing that is true of the endpoint. <see cref="DriverHealthState.Problem"/>
/// when anything is faulted; otherwise <see cref="DriverHealthState.Unknown"/> when
/// nothing has been reported at all; otherwise <see cref="DriverHealthState.Healthy"/>.
/// </param>
/// <param name="Faults">The drivers that count as faults, worst-attributable first.</param>
/// <param name="DriverFaultCount">Faults attributed to driver software.</param>
/// <param name="DeviceFaultCount">Faults attributed to hardware.</param>
/// <param name="IndeterminateFaultCount">Faults the platform declined to attribute.</param>
/// <param name="DisabledCount">
/// Devices administratively disabled. Reported separately and never counted as a
/// fault: this platform disables devices itself, and its own USB restriction must
/// not read as damage.
/// </param>
/// <param name="UnknownCount">Devices whose problem state could not be read.</param>
/// <param name="TotalCount">Every device considered.</param>
public sealed record DriverHealthResult(
    DriverHealthState OverallState,
    IReadOnlyList<DriverFinding> Faults,
    int DriverFaultCount,
    int DeviceFaultCount,
    int IndeterminateFaultCount,
    int DisabledCount,
    int UnknownCount,
    int TotalCount);

/// <summary>
/// Rolls per-device driver verdicts up into one endpoint verdict.
/// </summary>
/// <remarks>
/// <para>
/// Computed on read, never stored -- the same stance as
/// <see cref="DeviceSecurityPosture.ComplianceScore"/>. Re-classifying a problem
/// code then needs no data migration, and the stored rows stay what the endpoint
/// actually said rather than what an older build made of it.
/// </para>
/// <para>
/// An endpoint that has reported nothing is <see cref="DriverHealthState.Unknown"/>,
/// not healthy, for the same reason posture starts Unknown: absence of evidence is
/// not evidence of health.
/// </para>
/// </remarks>
public static class DriverHealthSummary
{
    public static DriverHealthResult Evaluate(IReadOnlyCollection<DriverView>? drivers)
    {
        if (drivers is null || drivers.Count == 0)
        {
            return new DriverHealthResult(
                DriverHealthState.Unknown, [], 0, 0, 0, 0, 0, 0);
        }

        var findings = drivers
            .Select(d => new DriverFinding(
                d.InstanceId, d.DeviceName, d.DeviceClass, DriverHealth.Classify(d.ProblemCode)))
            .ToList();

        var faults = findings
            .Where(f => f.Verdict.CountsAsFault)
            // Driver faults first: they are the ones an administrator can act on
            // from this platform. Then device faults, then the unattributed.
            .OrderBy(f => f.Verdict.FaultKind switch
            {
                DriverFaultKind.Driver => 0,
                DriverFaultKind.Device => 1,
                _ => 2,
            })
            .ThenBy(f => f.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unknown = findings.Count(f => f.Verdict.State == DriverHealthState.Unknown);

        var overall = faults.Count > 0
            ? DriverHealthState.Problem
            // Everything we could read is fine. If nothing could be read at all,
            // that is not health -- it is silence.
            : unknown == findings.Count
                ? DriverHealthState.Unknown
                : DriverHealthState.Healthy;

        return new DriverHealthResult(
            overall,
            faults,
            DriverFaultCount: faults.Count(f => f.Verdict.FaultKind == DriverFaultKind.Driver),
            DeviceFaultCount: faults.Count(f => f.Verdict.FaultKind == DriverFaultKind.Device),
            IndeterminateFaultCount: faults.Count(f => f.Verdict.FaultKind == DriverFaultKind.Indeterminate),
            DisabledCount: findings.Count(f => f.Verdict.State == DriverHealthState.Disabled),
            UnknownCount: unknown,
            TotalCount: findings.Count);
    }
}
