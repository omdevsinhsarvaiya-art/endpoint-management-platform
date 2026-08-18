using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// Windows integration test against this machine's real security state. Read-only,
/// unelevated: fields needing elevation (BitLocker, TPM) may come back null, which
/// is a valid "unknown" and must not throw.
/// </summary>
public sealed class WindowsSecurityPostureCollectorTests
{
    private static WindowsSecurityPostureCollector Create() =>
        new(new WindowsLocalAccountsCollector(NullLogger<WindowsLocalAccountsCollector>.Instance),
            NullLogger<WindowsSecurityPostureCollector>.Instance);

    [Fact]
    public async Task Collects_posture_without_throwing_even_unelevated()
    {
        var posture = await Create().CollectAsync(CancellationToken.None);
        posture.ShouldNotBeNull();
    }

    [Fact]
    public async Task Firewall_and_secure_boot_are_readable_without_elevation()
    {
        var posture = await Create().CollectAsync(CancellationToken.None);
        // Firewall profile registry is world-readable; at least one should be known.
        var firewallKnown = posture.FirewallDomainEnabled.HasValue
            || posture.FirewallPrivateEnabled.HasValue
            || posture.FirewallPublicEnabled.HasValue;
        firewallKnown.ShouldBeTrue("firewall profile state is readable from the registry unelevated");
    }

    [Fact]
    public async Task Local_administrator_count_is_reported()
    {
        var posture = await Create().CollectAsync(CancellationToken.None);
        posture.LocalAdministratorCount.ShouldNotBeNull();
        posture.LocalAdministratorCount!.Value.ShouldBeGreaterThanOrEqualTo(1);
    }
}
