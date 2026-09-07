using System.Diagnostics;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// A test that can only distinguish an uncapped enumeration from a capped one
/// on a machine running more processes than the cap.
/// </summary>
/// <remarks>
/// The inventory summary stops at 500 processes. On a machine below that, the
/// complete enumeration and the summary return the same count and the test
/// proves nothing either way -- so it is reported as SKIPPED with the reason,
/// rather than passing and claiming coverage that never ran. The developer
/// workstation this was written on runs ~540.
/// </remarks>
public sealed class ManyProcessesFactAttribute : FactAttribute
{
    public const int Threshold = 500;

    public ManyProcessesFactAttribute()
    {
        var count = Process.GetProcesses().Length;
        if (count <= Threshold)
        {
            Skip = $"Requires a machine running more than {Threshold} processes (this one runs {count}); " +
                   "the uncapped enumeration was not distinguished from the cap in this run.";
        }
    }
}
