using System.ComponentModel;
using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Tasks;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Tasks;

/// <summary>
/// Removing an application: stop it as Force Stop would, then uninstall it.
/// </summary>
/// <remarks>
/// The executor is proven against fakes for all three of its collaborators. What
/// matters here is the order (nothing is uninstalled before its processes are
/// gone), what happens when the stop step has nothing to do or cannot run, that
/// every answer the remover can give becomes the right message and result JSON,
/// and that a task naming anything other than the two closed methods -- or
/// naming them incompletely -- stops and removes nothing.
/// </remarks>
public sealed class RemoveApplicationExecutorTests
{
    private const string ChromeDir = @"C:\Program Files\Google\Chrome\Application";
    private const string ProductCode = "{2C1F6A3E-9B47-4D0A-8E51-3F2A7B9C4D60}";
    private const string PackageFullName = "Contoso.App_1.2.3.0_x64__abcdefghijklm";

    /// <summary>Live process list, as the endpoint would enumerate it now.</summary>
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
        public HashSet<int> RefuseAsGone { get; } = [];

        /// <summary>
        /// Pids the operating system will not let anyone terminate. A protected
        /// process denies even LocalSystem, and the real control surfaces that as
        /// a <see cref="Win32Exception"/> -- not one of the benign kinds the
        /// stopper swallows.
        /// </summary>
        public HashSet<int> RefuseAsProtected { get; } = [];

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

            if (RefuseAsProtected.Contains(processId))
            {
                throw new Win32Exception(5); // ERROR_ACCESS_DENIED: "Access is denied."
            }

            Terminated.Add((processId, expectedImageName));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Records which identity it was handed and, for the ordering proof, how many
    /// processes had already been terminated when it was asked.
    /// </summary>
    private sealed class FakeRemover(FakeControl control) : IApplicationRemover
    {
        public ApplicationRemovalOutcome Outcome { get; set; } = new(ApplicationRemovalResult.Removed, 0, "removed");

        public List<(string Method, string Identity)> Calls { get; } = [];

        public int TerminatedWhenAsked { get; private set; } = -1;

        public ValueTask<ApplicationRemovalOutcome> RemoveWindowsInstallerProductAsync(
            string productCode, CancellationToken cancellationToken = default)
        {
            Calls.Add(("WindowsInstaller", productCode));
            TerminatedWhenAsked = control.Terminated.Count;
            return ValueTask.FromResult(Outcome);
        }

        public ValueTask<ApplicationRemovalOutcome> RemovePackageAsync(
            string packageFullName, CancellationToken cancellationToken = default)
        {
            Calls.Add(("Package", packageFullName));
            TerminatedWhenAsked = control.Terminated.Count;
            return ValueTask.FromResult(Outcome);
        }
    }

    private static InventoryProcess Proc(int pid, string name, string? path) => new(pid, name, 100_000, path);

    private static AgentTask Task_(
        string name = "Google Chrome",
        string? location = ChromeDir,
        string method = "WindowsInstaller",
        string? productCode = ProductCode,
        string? packageFullName = null) => new(
        Guid.CreateVersion7(), "RemoveApplication",
        JsonSerializer.Serialize(
            new
            {
                applicationName = name,
                publisher = "Google LLC",
                installLocation = location,
                method,
                productCode,
                packageFullName,
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static RemoveApplicationExecutor Executor(FakeCollector collector, FakeControl control, FakeRemover remover) =>
        new(collector, control, remover, NullLogger<RemoveApplicationExecutor>.Instance);

    private static FakeCollector RunningChrome() => new(
        Proc(1000, "chrome", $@"{ChromeDir}\chrome.exe"),
        Proc(1001, "chrome", $@"{ChromeDir}\chrome.exe"),
        Proc(2000, "explorer", @"C:\Windows\explorer.exe"));

    // -------------------------------------------------------------- happy path

    /// <summary>
    /// The order is the promise. Every process of the application is gone before
    /// the remover is asked; the message says both halves happened.
    /// </summary>
    [Fact]
    public async Task Processes_are_terminated_before_the_remover_is_asked()
    {
        var collector = RunningChrome();
        var control = new FakeControl();
        var remover = new FakeRemover(control);

        var result = await Executor(collector, control, remover).ExecuteAsync(Task_());

        result.Succeeded.ShouldBeTrue();
        result.Message.ShouldBe("'Google Chrome' removed: 2 process(es) terminated; uninstalled.");
        control.Terminated.Select(t => t.Pid).ShouldBe([1000, 1001], ignoreOrder: true);
        remover.TerminatedWhenAsked.ShouldBe(2);
        remover.Calls.ShouldBe([("WindowsInstaller", ProductCode)]);
    }

    /// <summary>
    /// The stop step is the Force Stop one: the complete enumeration, not the
    /// capped inventory summary.
    /// </summary>
    [Fact]
    public async Task The_stop_step_enumerates_every_process_not_the_inventory_summary()
    {
        var collector = RunningChrome();
        var control = new FakeControl();

        await Executor(collector, control, new FakeRemover(control)).ExecuteAsync(Task_());

        collector.CompleteEnumerationCalled.ShouldBeTrue();
        collector.CappedSummaryCalled.ShouldBeFalse();
    }

    /// <summary>
    /// An application that is not running is still removed; "not running" is
    /// what the stop step found, not a reason to stop.
    /// </summary>
    [Fact]
    public async Task An_application_that_is_not_running_is_still_removed()
    {
        var collector = new FakeCollector(Proc(2000, "explorer", @"C:\Windows\explorer.exe"));
        var control = new FakeControl();
        var remover = new FakeRemover(control);

        var result = await Executor(collector, control, remover).ExecuteAsync(Task_());

        result.Succeeded.ShouldBeTrue();
        result.Message.ShouldBe("'Google Chrome' removed: not running; uninstalled.");
        control.Terminated.ShouldBeEmpty();
        remover.Calls.Count.ShouldBe(1);
    }

    /// <summary>
    /// Unlike Force Stop, an unusable install location is not a failure: the
    /// stop step is skipped, the result says so, and the uninstall proceeds --
    /// the installer's record is what is being removed, and it needs no directory.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Program Files")]
    public async Task An_unusable_install_location_skips_the_stop_step_and_still_removes(string? location)
    {
        var collector = RunningChrome();
        var control = new FakeControl();
        var remover = new FakeRemover(control);

        var result = await Executor(collector, control, remover).ExecuteAsync(Task_(location: location));

        result.Succeeded.ShouldBeTrue();
        result.Message.ShouldBe("'Google Chrome' removed: not stopped: no install location; uninstalled.");
        collector.CompleteEnumerationCalled.ShouldBeFalse("nothing is enumerated when nothing can be matched");
        control.Terminated.ShouldBeEmpty();
        remover.Calls.Count.ShouldBe(1);
    }

    /// <summary>
    /// Every process vanishing between enumeration and termination fails Force
    /// Stop; here it is simply "not running" on the way to the uninstall.
    /// </summary>
    [Fact]
    public async Task Processes_that_end_before_they_can_be_terminated_do_not_block_the_removal()
    {
        var collector = new FakeCollector(Proc(1000, "chrome", $@"{ChromeDir}\chrome.exe"));
        var control = new FakeControl();
        control.RefuseAsGone.Add(1000);
        var remover = new FakeRemover(control);

        var result = await Executor(collector, control, remover).ExecuteAsync(Task_());

        result.Succeeded.ShouldBeTrue();
        result.Message.ShouldBe("'Google Chrome' removed: not running; uninstalled.");
        remover.Calls.Count.ShouldBe(1);
    }

    /// <summary>
    /// A process the operating system will not let anyone terminate -- a
    /// protected process denies even LocalSystem -- is not the end of the task.
    /// The uninstall is the point: the product's own uninstaller stops what it
    /// owns, so Windows Installer gets to give the verdict, and the summary says
    /// honestly that the application was not fully stopped first.
    /// </summary>
    [Fact]
    public async Task A_stop_step_that_fails_does_not_abort_the_uninstall()
    {
        var collector = RunningChrome();
        var control = new FakeControl();
        control.RefuseAsProtected.Add(1000);
        var remover = new FakeRemover(control);

        var result = await Executor(collector, control, remover).ExecuteAsync(Task_());

        result.Succeeded.ShouldBeTrue();
        result.Message.ShouldBe("'Google Chrome' removed: not fully stopped; uninstalled.");
        remover.Calls.ShouldBe([("WindowsInstaller", ProductCode)]);
        // Nothing counted the terminations that a thrown-out step did not finish.
        result.ResultJson.ShouldBe("""{"method":"WindowsInstaller","processesTerminated":0,"result":"Removed","code":0}""");
    }

    /// <summary>
    /// Cancellation is the exception to that rule: an operator who cancelled the
    /// task wants nothing uninstalled, so it propagates rather than becoming a
    /// stop step that "did not complete".
    /// </summary>
    [Fact]
    public async Task A_cancelled_stop_step_does_not_go_on_to_uninstall()
    {
        var collector = RunningChrome();
        var control = new FakeControl();
        var remover = new FakeRemover(control);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await Executor(collector, control, remover).ExecuteAsync(Task_(), cancellation.Token));

        remover.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_package_method_routes_to_the_package_remover()
    {
        var collector = RunningChrome();
        var control = new FakeControl();
        var remover = new FakeRemover(control);

        var result = await Executor(collector, control, remover).ExecuteAsync(
            Task_(method: "Package", productCode: null, packageFullName: PackageFullName));

        result.Succeeded.ShouldBeTrue();
        remover.Calls.ShouldBe([("Package", PackageFullName)]);
        result.ResultJson.ShouldBe("""{"method":"Package","processesTerminated":2,"result":"Removed","code":0}""");
    }

    // ------------------------------------------------------- remover outcomes

    public static TheoryData<ApplicationRemovalResult, string?, bool, string> Outcomes() => new()
    {
        { ApplicationRemovalResult.Removed, "removed", true, "'Google Chrome' removed: 2 process(es) terminated; uninstalled." },
        { ApplicationRemovalResult.RemovedRebootRequired, "removed; a restart is required to finish", true, "'Google Chrome' removed; a restart is required to finish." },
        { ApplicationRemovalResult.AlreadyRemoved, "the product is not installed on this device", true, "'Google Chrome' was already removed." },
        { ApplicationRemovalResult.Refused, "refused: this is the endpoint agent", false, "'Google Chrome' could not be removed: refused: this is the endpoint agent." },
        { ApplicationRemovalResult.Failed, "Windows Installer returned 1603", false, "'Google Chrome' could not be removed: Windows Installer returned 1603." },
        { ApplicationRemovalResult.Failed, "the package is not registered on this device.", false, "'Google Chrome' could not be removed: the package is not registered on this device." },
        { ApplicationRemovalResult.Failed, null, false, "'Google Chrome' could not be removed: the remover reported Failed." },
    };

    /// <summary>
    /// Each answer the remover can give becomes exactly one message and one
    /// success flag. The reason is the remover's own words; a trailing period on
    /// them is not doubled.
    /// </summary>
    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task Each_remover_outcome_maps_to_its_message(
        ApplicationRemovalResult outcome, string? detail, bool succeeded, string message)
    {
        var collector = RunningChrome();
        var control = new FakeControl();
        var remover = new FakeRemover(control) { Outcome = new ApplicationRemovalOutcome(outcome, 1603, detail) };

        var result = await Executor(collector, control, remover).ExecuteAsync(Task_());

        result.Succeeded.ShouldBe(succeeded);
        result.Message.ShouldBe(message);
        result.ResultJson.ShouldNotBeNull();
        result.ResultJson!.ShouldContain($"\"result\":\"{outcome}\"");
    }

    /// <summary>
    /// The result JSON is the structured record the server keeps: the method,
    /// how many processes were terminated, the remover's verdict and its code.
    /// </summary>
    [Fact]
    public async Task The_result_json_carries_method_count_result_and_code()
    {
        var collector = RunningChrome();
        var control = new FakeControl();
        var remover = new FakeRemover(control);

        var result = await Executor(collector, control, remover).ExecuteAsync(Task_());

        result.ResultJson.ShouldBe("""{"method":"WindowsInstaller","processesTerminated":2,"result":"Removed","code":0}""");
    }

    /// <summary>
    /// A failure after the stop step still records what the stop step did: the
    /// processes are gone whether or not the uninstall went through.
    /// </summary>
    [Fact]
    public async Task A_failed_removal_after_a_stop_still_reports_what_was_terminated()
    {
        var collector = RunningChrome();
        var control = new FakeControl();
        var remover = new FakeRemover(control)
        {
            Outcome = new ApplicationRemovalOutcome(ApplicationRemovalResult.Failed, 1618, "another installation is in progress"),
        };

        var result = await Executor(collector, control, remover).ExecuteAsync(Task_());

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldBe("'Google Chrome' could not be removed: another installation is in progress.");
        result.ResultJson.ShouldBe("""{"method":"WindowsInstaller","processesTerminated":2,"result":"Failed","code":1618}""");
        control.Terminated.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_null_code_is_reported_as_null()
    {
        var collector = new FakeCollector();
        var control = new FakeControl();
        var remover = new FakeRemover(control)
        {
            Outcome = new ApplicationRemovalOutcome(ApplicationRemovalResult.Refused, null, "refused: this is the endpoint agent"),
        };

        var result = await Executor(collector, control, remover).ExecuteAsync(Task_(location: null));

        result.ResultJson.ShouldBe("""{"method":"WindowsInstaller","processesTerminated":0,"result":"Refused","code":null}""");
    }

    // ---------------------------------------------------------------- safety

    /// <summary>
    /// The method set is closed. Anything else -- an uninstaller path, a script,
    /// a method a newer server knows -- is refused before a single process is
    /// touched, so a refused task cannot leave an application half-done.
    /// </summary>
    [Theory]
    [InlineData("UninstallString")]
    [InlineData("Script")]
    [InlineData("windowsinstaller")]
    [InlineData("Executable")]
    public async Task An_unknown_method_is_refused_without_stopping_or_removing_anything(string method)
    {
        var collector = RunningChrome();
        var control = new FakeControl();
        var remover = new FakeRemover(control);

        var result = await Executor(collector, control, remover).ExecuteAsync(
            Task_(method: method, productCode: ProductCode, packageFullName: PackageFullName));

        result.Succeeded.ShouldBeFalse();
        result.Message!.ShouldContain("not supported");
        collector.CompleteEnumerationCalled.ShouldBeFalse();
        control.Terminated.ShouldBeEmpty();
        remover.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_malformed_or_incomplete_payload_stops_and_removes_nothing()
    {
        var collector = RunningChrome();
        var control = new FakeControl();
        var remover = new FakeRemover(control);
        var executor = Executor(collector, control, remover);

        (await executor.ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "RemoveApplication", null)))
            .Succeeded.ShouldBeFalse();

        (await executor.ExecuteAsync(new AgentTask(Guid.CreateVersion7(), "RemoveApplication", "{ not json")))
            .Succeeded.ShouldBeFalse();

        // No method at all.
        (await executor.ExecuteAsync(new AgentTask(
            Guid.CreateVersion7(), "RemoveApplication", """{"applicationName":"Google Chrome","productCode":"{2C1F6A3E-9B47-4D0A-8E51-3F2A7B9C4D60}"}""")))
            .Succeeded.ShouldBeFalse();

        // No name.
        (await executor.ExecuteAsync(Task_(name: "")))
            .Succeeded.ShouldBeFalse();

        // A known method without the identity it needs.
        (await executor.ExecuteAsync(Task_(method: "WindowsInstaller", productCode: null, packageFullName: PackageFullName)))
            .Succeeded.ShouldBeFalse();
        (await executor.ExecuteAsync(Task_(method: "Package", productCode: ProductCode, packageFullName: null)))
            .Succeeded.ShouldBeFalse();
        (await executor.ExecuteAsync(Task_(method: "Package", productCode: ProductCode, packageFullName: "   ")))
            .Succeeded.ShouldBeFalse();

        collector.CompleteEnumerationCalled.ShouldBeFalse();
        control.Terminated.ShouldBeEmpty();
        remover.Calls.ShouldBeEmpty();
    }

    /// <summary>
    /// The task carries no pid and no uninstaller path, so there is nothing for
    /// an upstream caller to choose. This pins the contract that makes that true.
    /// </summary>
    [Fact]
    public async Task A_pid_or_uninstaller_path_in_the_payload_is_not_read_and_changes_nothing()
    {
        var collector = RunningChrome();
        var control = new FakeControl();
        var remover = new FakeRemover(control);

        var payload = JsonSerializer.Serialize(new
        {
            applicationName = "Google Chrome",
            installLocation = ChromeDir,
            method = "WindowsInstaller",
            productCode = ProductCode,
            // Not part of the contract; must have no effect.
            processId = 2000,
            expectedImageName = "explorer",
            uninstallString = @"C:\Windows\System32\cmd.exe /c evil",
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        await Executor(collector, control, remover).ExecuteAsync(
            new AgentTask(Guid.CreateVersion7(), "RemoveApplication", payload));

        control.Terminated.Select(t => t.Pid).ShouldBe([1000, 1001], ignoreOrder: true);
        control.Terminated.ShouldNotContain(t => t.Pid == 2000);
        remover.Calls.ShouldBe([("WindowsInstaller", ProductCode)]);
    }

    [Fact]
    public void The_executor_answers_to_its_own_task_type()
    {
        var control = new FakeControl();
        Executor(new FakeCollector(), control, new FakeRemover(control)).TaskType.ShouldBe("RemoveApplication");
    }
}
