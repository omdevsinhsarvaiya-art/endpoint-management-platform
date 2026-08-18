using EndpointPlatform.Domain.Tasks;

namespace EndpointPlatform.Domain.Tests.Tasks;

public sealed class DeviceTaskTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Device = Guid.CreateVersion7();
    private static readonly Guid Actor = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private static DeviceTask Create(TimeSpan? ttl = null) =>
        DeviceTask.Create(Org, Device, DeviceTaskType.Ping, null, Actor, "admin@test",
            Now, ttl ?? TimeSpan.FromMinutes(5));

    [Fact]
    public void A_new_task_is_queued() => Create().Status.ShouldBe(DeviceTaskStatus.Queued);

    [Fact]
    public void Deliver_moves_queued_to_delivered()
    {
        var task = Create();
        task.TryDeliver(Now.AddSeconds(1)).ShouldBeTrue();
        task.Status.ShouldBe(DeviceTaskStatus.Delivered);
        task.DeliveredAt.ShouldBe(Now.AddSeconds(1));
    }

    [Fact]
    public void Deliver_after_expiry_expires_the_task_instead()
    {
        var task = Create(TimeSpan.FromSeconds(10));
        task.TryDeliver(Now.AddSeconds(11)).ShouldBeFalse();
        task.Status.ShouldBe(DeviceTaskStatus.Expired);
    }

    [Fact]
    public void A_task_can_only_be_delivered_once()
    {
        var task = Create();
        task.TryDeliver(Now.AddSeconds(1)).ShouldBeTrue();
        task.TryDeliver(Now.AddSeconds(2)).ShouldBeFalse("a claimed task must not be re-delivered to another poll");
    }

    [Fact]
    public void Complete_requires_delivered_state()
    {
        var task = Create();
        task.TryComplete(true, "ok", null, Now.AddSeconds(1)).ShouldBeFalse();

        task.TryDeliver(Now.AddSeconds(1));
        task.TryComplete(true, "ok", null, Now.AddSeconds(2)).ShouldBeTrue();
        task.Status.ShouldBe(DeviceTaskStatus.Succeeded);
    }

    [Fact]
    public void A_stale_or_replayed_result_cannot_overwrite_a_terminal_outcome()
    {
        var task = Create();
        task.TryDeliver(Now.AddSeconds(1));
        task.TryComplete(true, "ok", null, Now.AddSeconds(2)).ShouldBeTrue();

        task.TryComplete(false, "tampered", null, Now.AddSeconds(3)).ShouldBeFalse();
        task.Status.ShouldBe(DeviceTaskStatus.Succeeded, "a terminal task must be immutable");
    }

    [Fact]
    public void A_queued_task_can_be_cancelled_but_a_delivered_one_cannot()
    {
        var queued = Create();
        queued.TryCancel(Now.AddSeconds(1), "no longer needed").ShouldBeTrue();
        queued.Status.ShouldBe(DeviceTaskStatus.Cancelled);

        var delivered = Create();
        delivered.TryDeliver(Now.AddSeconds(1));
        delivered.TryCancel(Now.AddSeconds(2), "too late").ShouldBeFalse();
    }

    [Fact]
    public void Every_task_type_has_a_catalog_entry_with_a_permission()
    {
        foreach (var type in Enum.GetValues<DeviceTaskType>())
        {
            var definition = DeviceTaskCatalog.Require(type);
            definition.RequiredPermission.ShouldNotBeNullOrWhiteSpace();
            definition.DefaultTimeToLiveSeconds.ShouldBeGreaterThan(0);
        }
    }

    [Fact]
    public void Destructive_task_types_are_high_risk()
    {
        DeviceTaskCatalog.Require(DeviceTaskType.RestartDevice).HighRisk.ShouldBeTrue();
        DeviceTaskCatalog.Require(DeviceTaskType.ShutdownDevice).HighRisk.ShouldBeTrue();
        DeviceTaskCatalog.Require(DeviceTaskType.SignOutUser).HighRisk.ShouldBeTrue();
        DeviceTaskCatalog.Require(DeviceTaskType.Ping).HighRisk.ShouldBeFalse();
    }
}
