using System.Runtime.Versioning;
using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// The refusal that stands between a classification bug and a bricked machine.
/// </summary>
/// <remarks>
/// <para>
/// These run against the real enforcer on a real Windows machine and are safe
/// to do so: every instance ID below either names a hub — which the guard
/// refuses <em>before</em> touching SetupAPI — or names a device that does not
/// exist, which SetupAPI reports as absent and the enforcer treats as a no-op.
/// Nothing on the host is enabled, disabled or reconfigured.
/// </para>
/// <para>
/// The guard exists because disabling a hub disconnects everything attached to
/// it. That is not hypothetical: a classification defect did exactly this on the
/// acceptance machine, taking down the webcam, fingerprint reader and Bluetooth
/// radio along with the USB stick that triggered it.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsUsbEnforcerGuardTests
{
    private static WindowsUsbPolicyEnforcer Enforcer() =>
        new(NullLogger<WindowsUsbPolicyEnforcer>.Instance);

    [Theory]
    [InlineData(@"USB\ROOT_HUB30\4&70DF8FF&0&0")]
    [InlineData(@"USB\ROOT_HUB30\4&3AF0ECE5&0&0")]
    [InlineData(@"USB\ROOT_HUB20\4&6A987E4&0")]
    [InlineData(@"USB\ROOT_HUB\4&24D6EB65&0")]
    [InlineData(@"usb\root_hub30\4&AAAA&0&0")]
    public void Restrict_refuses_a_root_hub_outright(string instanceId)
    {
        var result = Enforcer().Restrict(instanceId);

        result.Succeeded.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("hub");
    }

    /// <summary>
    /// The refusal explains itself, because the operator reading it needs to know
    /// this was a refusal rather than a driver failure.
    /// </summary>
    [Fact]
    public void The_refusal_says_why()
    {
        var result = Enforcer().Restrict(@"USB\ROOT_HUB30\4&70DF8FF&0&0");

        result.Error!.ShouldContain("disconnect every device attached to it");
    }

    /// <summary>
    /// A device that is not a hub is not caught by the guard.
    /// </summary>
    /// <remarks>
    /// A guard that refused everything would also "pass" the tests above while
    /// silently disabling the whole feature, so the negative case is asserted
    /// too. This instance ID does not exist on any machine, so the enforcer
    /// reaches SetupAPI, finds nothing, and reports the absent-device no-op
    /// success — never a hub refusal.
    /// </remarks>
    [Fact]
    public void A_device_that_is_not_a_hub_is_not_refused_by_the_guard()
    {
        var result = Enforcer().Restrict(@"USB\VID_0781&PID_5581\NOTAREALDEVICE0001");

        // Absent device: a no-op success, and specifically not the hub refusal.
        result.Error?.ShouldNotContain("hub");
    }
}
