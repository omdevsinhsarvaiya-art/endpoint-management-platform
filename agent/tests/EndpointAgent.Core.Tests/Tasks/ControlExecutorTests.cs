using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Tasks;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Tasks;

public sealed class ControlExecutorTests
{
    private sealed class FakeControl : IServiceProcessControl
    {
        public string? LastOp { get; private set; }
        public Task StartServiceAsync(string s, CancellationToken c = default) { LastOp = $"start:{s}"; return Task.CompletedTask; }
        public Task StopServiceAsync(string s, CancellationToken c = default) { LastOp = $"stop:{s}"; return Task.CompletedTask; }
        public Task RestartServiceAsync(string s, CancellationToken c = default) { LastOp = $"restart:{s}"; return Task.CompletedTask; }
        public Task TerminateProcessAsync(int pid, string img, CancellationToken c = default) { LastOp = $"kill:{pid}:{img}"; return Task.CompletedTask; }
    }

    [Fact]
    public async Task Control_service_start_dispatches_to_the_control_api()
    {
        var control = new FakeControl();
        var exec = new ControlServiceTaskExecutor(control, NullLogger<ControlServiceTaskExecutor>.Instance);

        var result = await exec.ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "ControlService",
            """{"serviceName":"Spooler","action":"Start"}"""));

        result.Succeeded.ShouldBeTrue();
        control.LastOp.ShouldBe("start:Spooler");
    }

    [Fact]
    public async Task An_unknown_service_action_fails_without_calling_control()
    {
        var control = new FakeControl();
        var exec = new ControlServiceTaskExecutor(control, NullLogger<ControlServiceTaskExecutor>.Instance);

        var result = await exec.ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "ControlService",
            """{"serviceName":"Spooler","action":"Nuke"}"""));

        result.Succeeded.ShouldBeFalse();
        control.LastOp.ShouldBeNull();
    }

    [Fact]
    public async Task A_malformed_control_payload_is_reported_failed()
    {
        var exec = new ControlServiceTaskExecutor(new FakeControl(), NullLogger<ControlServiceTaskExecutor>.Instance);
        (await exec.ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "ControlService", "not json"))).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task Terminate_process_passes_the_expected_image_guard_through()
    {
        var control = new FakeControl();
        var exec = new TerminateProcessTaskExecutor(control, NullLogger<TerminateProcessTaskExecutor>.Instance);

        var result = await exec.ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "TerminateProcess",
            """{"processId":4321,"expectedImageName":"notepad.exe"}"""));

        result.Succeeded.ShouldBeTrue();
        control.LastOp.ShouldBe("kill:4321:notepad.exe");
    }
}
