using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Tasks;

namespace EndpointPlatform.Domain.Tests.Tasks;

/// <summary>
/// The driver task's catalogue entry and the minimum-agent-version gate.
///
/// The gate is a courtesy rather than a safety boundary — an agent without the
/// executor still fails closed on an unknown task type — but it is the difference
/// between an operator seeing "your agent is too old" and seeing a failed task
/// indistinguishable from a driver that would not install.
/// </summary>
public sealed class DriverTaskCatalogTests
{
    private static DeviceTaskDefinition Driver =>
        DeviceTaskCatalog.Require(DeviceTaskType.InstallDriverPackage);

    [Fact]
    public void Driver_installation_requires_the_driver_management_permission_and_is_high_risk()
    {
        Driver.RequiredPermission.ShouldBe(Permissions.Driver.Manage);
        Driver.HighRisk.ShouldBeTrue();
    }

    [Fact]
    public void Driver_installation_declares_a_minimum_agent_version()
    {
        Driver.MinimumAgentVersion.ShouldNotBeNullOrWhiteSpace();
        Version.TryParse(Driver.MinimumAgentVersion, out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("1.3.0")]
    [InlineData("1.3.1")]
    [InlineData("1.4.0")]
    [InlineData("2.0.0")]
    public void An_agent_at_or_above_the_minimum_is_supported(string agentVersion)
    {
        DeviceTaskCatalog.IsSupportedBy(Driver, agentVersion).ShouldBeTrue();
    }

    [Theory]
    [InlineData("1.2.9")]
    [InlineData("1.2.0")]
    [InlineData("1.0.0")]
    [InlineData("0.9.9")]
    public void An_agent_below_the_minimum_is_not_supported(string agentVersion)
    {
        DeviceTaskCatalog.IsSupportedBy(Driver, agentVersion).ShouldBeFalse();
    }

    /// <summary>
    /// An agent that will not say what it is has not demonstrated that it can do the
    /// work. Refusing is the only answer that cannot queue a task nothing will run.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData("v1.3.0")]
    [InlineData("latest")]
    public void An_unparseable_agent_version_is_refused(string? agentVersion)
    {
        DeviceTaskCatalog.IsSupportedBy(Driver, agentVersion).ShouldBeFalse();
    }

    /// <summary>CI builds carry suffixes; the numeric part is what decides.</summary>
    [Theory]
    [InlineData("1.3.0-beta.2")]
    [InlineData("1.3.0+ci.451")]
    [InlineData("1.4.0-rc1")]
    public void A_pre_release_or_build_suffix_does_not_defeat_the_comparison(string agentVersion)
    {
        DeviceTaskCatalog.IsSupportedBy(Driver, agentVersion).ShouldBeTrue();
    }

    /// <summary>
    /// Every task without a declared minimum keeps admitting every agent, so adding
    /// the gate cannot have changed the behaviour of any existing task type.
    /// </summary>
    [Fact]
    public void Tasks_without_a_minimum_admit_any_agent_version()
    {
        foreach (var definition in DeviceTaskCatalog.All.Values.Where(d => d.MinimumAgentVersion is null))
        {
            DeviceTaskCatalog.IsSupportedBy(definition, "0.0.1").ShouldBeTrue(
                $"{definition.Type} declares no minimum and must not have gained one implicitly");

            DeviceTaskCatalog.IsSupportedBy(definition, null).ShouldBeTrue();
        }
    }

    /// <summary>
    /// The tasks Milestones 11 and 12 rely on must not have acquired a version gate
    /// as a side effect of this change.
    /// </summary>
    [Theory]
    [InlineData(DeviceTaskType.ApplyUsbPolicy)]
    [InlineData(DeviceTaskType.ApplyLocalAdminElevation)]
    [InlineData(DeviceTaskType.RefreshInventory)]
    [InlineData(DeviceTaskType.InstallPackage)]
    public void Existing_task_types_are_unchanged(DeviceTaskType type)
    {
        DeviceTaskCatalog.Require(type).MinimumAgentVersion.ShouldBeNull();
    }
}
