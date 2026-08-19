using System.Security.Principal;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// A test that can only prove anything when the run is elevated.
/// </summary>
/// <remarks>
/// Managing local accounts requires administrator privilege. An unelevated run
/// cannot exercise those paths, so the test is reported as SKIPPED rather than
/// passed: an early <c>return</c> would show green and claim coverage that never
/// ran, which is precisely how a silently-ignored Windows flag survived earlier.
/// </remarks>
public sealed class ElevatedFactAttribute : FactAttribute
{
    public ElevatedFactAttribute()
    {
        if (!IsElevated())
        {
            Skip = "Requires an elevated test run; real Windows account state was not verified.";
        }
    }

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
