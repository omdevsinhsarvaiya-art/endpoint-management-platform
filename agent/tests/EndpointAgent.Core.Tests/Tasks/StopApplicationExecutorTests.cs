using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Tasks;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Tasks;

/// <summary>
/// Stopping an application by resolving its processes at execution time.
/// </summary>
/// <remarks>
/// These tests exist because of a staleness bug, so most of them are about the
/// gap between "what the server last heard" and "what is running now": a process
/// that exited, one that restarted under a different pid, and a pid that has been
/// reused by something unrelated. The executor must handle all three without
/// terminating the wrong thing and without reporting a success it did not achieve.
/// </remarks>
public sealed class StopApplicationExecutorTests
{
    private const string ChromeDir = @"C:\Program Files\Google\Chrome\Application";

    /// <summary>
    /// Live process list, as the endpoint would enumerate it now.
    /// </summary>
    /// <remarks>
    /// The capped method models the real Windows provider -- largest first, never
    /// more than 500 -- rather than handing back everything. A fake that returned
    /// the full list from both methods would let the executor call the wrong one
    /// and still pass every test, which is precisely how the cap went unnoticed.
    /// </remarks>
    private sealed class FakeCollector(params InventoryProcess[] processes) : IServiceProcessCollector
    {
        public ValueTask<IReadOnlyList<InventoryService>> CollectServicesAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<InventoryService>>([]);

        public bool CappedSummaryCalled { get; private set; }

        public bool CompleteEnumerationCalled { get; private set; }

        public ValueTask<IReadOnlyList<InventoryProcess>> CollectProcessesAsync(
            int max, CancellationToken cancellationToken = default)
        {
            CappedSummaryCalled = true;
            return ValueTask.FromResult<IReadOnlyList<InventoryProcess>>(
                processes.OrderByDescending(p => p.WorkingSetBytes).Take(Math.Clamp(max, 1, 500)).ToArray());
        }

        public ValueTask<IReadOnlyList<InventoryProcess>> CollectAllProcessesAsync(
            CancellationToken cancellationToken = default)
        {
            CompleteEnumerationCalled = true;
            return ValueTask.FromResult<IReadOnlyList<InventoryProcess>>(processes);
        }
    }

    private sealed class FakeControl : IServiceProcessControl
    {
        /// <summary>Pids whose image no longer matches, as the real guard would find.</summary>
        public HashSet<int> RefuseAsMismatched { get; } = [];

        /// <summary>Pids that have exited since enumeration.</summary>
        public HashSet<int> RefuseAsGone { get; } = [];

        public List<(int Pid, string Image)> Terminated { get; } = [];

        public Task StartServiceAsync(string serviceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopServiceAsync(string serviceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RestartServiceAsync(string serviceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task TerminateProcessAsync(
            int processId, string expectedImageName, CancellationToken cancellationToken = default)
        {
            if (RefuseAsGone.Contains(processId))
            {
                throw new ArgumentException($"No process with id {processId}.");
            }

            if (RefuseAsMismatched.Contains(processId))
            {
                throw new InvalidOperationException(
                    $"Process {processId} is not the expected '{expectedImageName}'. Refusing to terminate.");
            }

            Terminated.Add((processId, expectedImageName));
            return Task.CompletedTask;
        }
    }

    private static InventoryProcess Proc(int pid, string name, string? path) => new(pid, name, 100_000, path);

    private static AgentTask Task_(string name = "Google Chrome", string location = ChromeDir) => new(
        Guid.CreateVersion7(), "StopApplication",
        System.Text.Json.JsonSerializer.Serialize(
            new { applicationName = name, publisher = "Google LLC", installLocation = location },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

    private static StopApplicationExecutor Executor(FakeCollector collector, FakeControl control) =>
        new(collector, control, NullLogger<StopApplicationExecutor>.Instance);

    // -------------------------------------------------------------- happy path

    /// <summary>
    /// Every process the application owns is stopped. A browser is a parent and
    /// many children; stopping only the first would leave it running while
    /// reporting success.
    /// </summary>
    [Fact]
    public async Task All_of_an_applications_processes_are_terminated()
    {
        var collector = new FakeCollector(
            Proc(1000, "chrome", $@"{ChromeDir}\chrome.exe"),
            Proc(1001, "chrome", $@"{ChromeDir}\chrome.exe"),
            Proc(2000, "explorer", @"C:\Windows\explorer.exe"));
        var control = new FakeControl();

        var result = await Executor(collector, control).ExecuteAsync(Task_());

        result.Succeeded.ShouldBeTrue();
        control.Terminated.Select(t => t.Pid).ShouldBe([1000, 1001], ignoreOrder: true);
        control.Terminated.ShouldNotContain(t => t.Pid == 2000);
    }

    /// <summary>
    /// The enumeration must be the complete one, not the inventory summary. The
    /// earlier version of this test asserted that the executor <em>asked</em> for
    /// more than the cap -- and the Windows provider clamped the answer to 500
    /// regardless, so the test was green while the behaviour was wrong. What is
    /// asserted now is which method ran.
    /// </summary>
    [Fact]
    public async Task The_executor_enumerates_every_process_not_the_inventory_summary()
    {
        var collector = new FakeCollector(Proc(1000, "chrome", $@"{ChromeDir}\chrome.exe"));

        await Executor(collector, new FakeControl()).ExecuteAsync(Task_());

        collector.CompleteEnumerationCalled.ShouldBeTrue();
        collector.CappedSummaryCalled.ShouldBeFalse("the capped summary can miss an application's own processes");
    }

    /// <summary>
    /// The case the cap actually produces. A machine with 700 processes; the
    /// browser is large and ranks near the top, its helper is tiny and ranks at
    /// 650 -- past the 500 the summary would return. Both must be stopped, and
    /// the result must not claim success with the helper still running.
    /// </summary>
    [Fact]
    public async Task A_low_memory_helper_ranked_past_the_inventory_cap_is_still_terminated()
    {
        // 698 unrelated processes with descending working sets, the browser at
        // the top, and the helper placed so that ~650 processes outrank it.
        var unrelated = Enumerable.Range(0, 698)
            .Select(i => new InventoryProcess(10_000 + i, $"svc{i}", 50_000_000 - i * 10_000, @"C:\Windows\System32\svchost.exe"));
        var browser = new InventoryProcess(1000, "chrome", 900_000_000, $@"{ChromeDir}\chrome.exe");
        var helper = new InventoryProcess(1001, "chrome", 50_000_000 - 650 * 10_000 - 5_000, $@"{ChromeDir}\chrome.exe");

        var all = unrelated.Append(browser).Append(helper).ToArray();
        all.Length.ShouldBe(700);
        all.OrderByDescending(p => p.WorkingSetBytes).ToList().IndexOf(helper).ShouldBeInRange(640, 660);

        var collector = new FakeCollector(all);
        var control = new FakeControl();

        var result = await Executor(collector, control).ExecuteAsync(Task_());

        result.Succeeded.ShouldBeTrue();
        control.Terminated.Select(t => t.Pid).ShouldBe([1000, 1001], ignoreOrder: true);
        control.Terminated.ShouldNotContain(t => t.Pid >= 10_000, "no unrelated process is touched");
    }

    // ------------------------------------------------------------- staleness

    /// <summary>
    /// The bug this executor exists for. A pid the server saw ninety minutes ago
    /// is irrelevant: resolution happens now, so a process that has restarted
    /// under a new pid is still found and stopped.
    /// </summary>
    [Fact]
    public async Task A_process_that_restarted_under_a_new_pid_is_still_found()
    {
        // The server's snapshot said 1000. The machine has moved on.
        var collector = new FakeCollector(Proc(7777, "chrome", $@"{ChromeDir}\chrome.exe"));
        var control = new FakeControl();

        var result = await Executor(collector, control).ExecuteAsync(Task_());

        result.Succeeded.ShouldBeTrue();
        control.Terminated.Single().Pid.ShouldBe(7777);
    }

    /// <summary>
    /// An application that has exited since the snapshot is not an error. The
    /// operator wanted it not running, and it is not running.
    /// </summary>
    [Fact]
    public async Task An_application_that_is_no_longer_running_succeeds_without_terminating_anything()
    {
        var collector = new FakeCollector(Proc(2000, "explorer", @"C:\Windows\explorer.exe"));
        var control = new FakeControl();

        var result = await Executor(collector, control).ExecuteAsync(Task_());

        result.Succeeded.ShouldBeTrue();
        result.Message.ShouldNotBeNull();
        result.Message!.Contains("not running", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
        control.Terminated.ShouldBeEmpty();
    }

    /// <summary>
    /// A pid reused by an unrelated process between enumeration and termination is
    /// refused by the guard. The rest of the application is still stopped, and the
    /// refusal is not reported as a machine fault.
    /// </summary>
    [Fact]
    public async Task A_pid_reused_since_enumeration_is_refused_without_failing_the_rest()
    {
        var collector = new FakeCollector(
            Proc(1000, "chrome", $@"{ChromeDir}\chrome.exe"),
            Proc(1001, "chrome", $@"{ChromeDir}\chrome.exe"));
        var control = new FakeControl();
        control.RefuseAsMismatched.Add(1001);

        var result = await Executor(collector, control).ExecuteAsync(Task_());

        result.Succeeded.ShouldBeTrue();
        control.Terminated.Single().Pid.ShouldBe(1000);
        result.Message!.Contains("already ended", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
    }

    /// <summary>
    /// When every process vanished in the gap, nothing was achieved and the result
    /// says so rather than claiming a success.
    /// </summary>
    [Fact]
    public async Task When_every_process_disappears_the_result_is_a_failure_not_a_false_success()
    {
        var collector = new FakeCollector(Proc(1000, "chrome", $@"{ChromeDir}\chrome.exe"));
        var control = new FakeControl();
        control.RefuseAsGone.Add(1000);

        var result = await Executor(collector, control).ExecuteAsync(Task_());

        result.Succeeded.ShouldBeFalse();
        control.Terminated.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- safety

    /// <summary>
    /// The endpoint re-applies the broad-root rule rather than trusting the task.
    /// This is the side that terminates, so this is the side that must be sure.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Program Files")]
    public async Task A_broad_install_location_in_the_payload_is_refused_on_the_endpoint(string location)
    {
        var collector = new FakeCollector(
            Proc(2000, "explorer", @"C:\Windows\explorer.exe"),
            Proc(2001, "svchost", @"C:\Windows\System32\svchost.exe"));
        var control = new FakeControl();

        var result = await Executor(collector, control).ExecuteAsync(Task_(location: location));

        result.Succeeded.ShouldBeFalse();
        control.Terminated.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_malformed_or_incomplete_payload_terminates_nothing()
    {
        var control = new FakeControl();
        var executor = Executor(new FakeCollector(Proc(1000, "chrome", $@"{ChromeDir}\chrome.exe")), control);

        (await executor.ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "StopApplication", null)))
            .Succeeded.ShouldBeFalse();

        (await executor.ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "StopApplication", "{ not json")))
            .Succeeded.ShouldBeFalse();

        (await executor.ExecuteAsync(new AgentTask(
            Guid.CreateVersion7(), "StopApplication", """{"applicationName":"","installLocation":""}""")))
            .Succeeded.ShouldBeFalse();

        control.Terminated.ShouldBeEmpty();
    }

    /// <summary>
    /// A directory cannot claim a sibling whose name merely starts the same way.
    /// </summary>
    [Fact]
    public async Task A_directory_prefix_collision_does_not_terminate_the_neighbour()
    {
        var collector = new FakeCollector(
            Proc(1000, "app", @"C:\Program Files\Contoso\app.exe"),
            Proc(1001, "other", @"C:\Program Files\ContosoExtra\other.exe"));
        var control = new FakeControl();

        await Executor(collector, control).ExecuteAsync(
            Task_(name: "Contoso App", location: @"C:\Program Files\Contoso"));

        control.Terminated.Single().Pid.ShouldBe(1000);
    }

    /// <summary>
    /// The task carries no pid, so there is nothing for an upstream caller to
    /// choose. This pins the contract that makes that true.
    /// </summary>
    [Fact]
    public async Task A_pid_in_the_payload_is_not_read_and_changes_nothing()
    {
        var collector = new FakeCollector(
            Proc(1000, "chrome", $@"{ChromeDir}\chrome.exe"),
            Proc(2000, "explorer", @"C:\Windows\explorer.exe"));
        var control = new FakeControl();

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            applicationName = "Google Chrome",
            installLocation = ChromeDir,
            // Not part of the contract; must have no effect.
            processId = 2000,
            expectedImageName = "explorer",
            executablePath = @"C:\Windows\explorer.exe",
        }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        await Executor(collector, control).ExecuteAsync(
            new AgentTask(Guid.CreateVersion7(), "StopApplication", payload));

        control.Terminated.Single().Pid.ShouldBe(1000);
        control.Terminated.ShouldNotContain(t => t.Pid == 2000);
    }

    [Fact]
    public void The_executor_answers_to_its_own_task_type()
    {
        Executor(new FakeCollector(), new FakeControl()).TaskType.ShouldBe("StopApplication");
    }
}
