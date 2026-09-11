using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Inventory;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Removes a named installed application: stops it exactly as Force Stop would,
/// then uninstalls it through <see cref="IApplicationRemover"/>.
/// </summary>
/// <remarks>
/// <para>
/// One task, two steps, in that order. Uninstalling a running application
/// either fails with its files in use or succeeds and asks for a restart;
/// stopping it first is what makes "removed" mean removed. The stop step is
/// <see cref="ApplicationStopper"/> -- the same enumeration, matcher and guard
/// as <see cref="StopApplicationExecutor"/> -- and, as there, "not running" is
/// not a failure. When the row has no usable install location the stop step is
/// skipped and the result says so; the uninstall still proceeds, because what is
/// being removed is the installer's record, and that does not need a directory.
/// </para>
/// <para>
/// For the same reason a stop step that <em>fails</em> does not abort the task
/// either. Terminating a protected process is refused even to LocalSystem, and
/// an uninstall that never happened because of it is a worse outcome than one
/// that ran with the application still up -- the product's own uninstaller stops
/// what it owns. The failure is logged, the summary says "not fully stopped",
/// and Windows Installer gets to give the real verdict. Cancellation is the one
/// exception: an operator who cancelled the task wants nothing uninstalled.
/// </para>
/// <para>
/// The task names a product code or a package full name that the platform's
/// own inventory recorded, plus a method saying which. It cannot name a path, a
/// command line or an uninstaller executable: a method this executor does not
/// know is refused before anything is stopped, and the remover refuses the agent
/// itself and operating-system components on its own judgement, whatever the
/// task says.
/// </para>
/// </remarks>
public sealed class RemoveApplicationExecutor(
    IServiceProcessCollector collector,
    IServiceProcessControl control,
    IApplicationRemover remover,
    ILogger<RemoveApplicationExecutor> logger) : ITaskExecutor
{
    private const string WindowsInstallerMethod = "WindowsInstaller";
    private const string PackageMethod = "Package";

    private readonly ApplicationStopper _stopper = new(collector, control, logger);

    public string TaskType => "RemoveApplication";

    public async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(task.PayloadJson))
        {
            return new AgentTaskResult(false, "Missing remove-application payload.", null);
        }

        string applicationName;
        string method;
        string? installLocation;
        string? productCode;
        string? packageFullName;
        try
        {
            using var doc = JsonDocument.Parse(task.PayloadJson);
            var root = doc.RootElement;
            applicationName = root.GetProperty("applicationName").GetString() ?? "";
            method = root.GetProperty("method").GetString() ?? "";
            installLocation = OptionalString(root, "installLocation");
            productCode = OptionalString(root, "productCode");
            packageFullName = OptionalString(root, "packageFullName");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new AgentTaskResult(false, "Malformed remove-application payload.", null);
        }

        if (string.IsNullOrWhiteSpace(applicationName) || string.IsNullOrWhiteSpace(method))
        {
            return new AgentTaskResult(false, "Remove-application payload is incomplete.", null);
        }

        // A closed set, settled before anything is stopped: a task that will be
        // refused must not terminate anything on the way. Anything else -- an
        // uninstaller path, a script, a method a newer server knows and this
        // agent does not -- is refused rather than guessed at.
        if (method is not (WindowsInstallerMethod or PackageMethod))
        {
            logger.LogWarning(
                "Remove-application task for {Application} names an unsupported method; refused.", applicationName);
            return new AgentTaskResult(
                false, $"Remove-application method '{method}' is not supported by this agent.", null);
        }

        var identity = method == WindowsInstallerMethod ? productCode : packageFullName;
        if (string.IsNullOrWhiteSpace(identity))
        {
            return new AgentTaskResult(false, "Remove-application payload is incomplete.", null);
        }

        // Step one: stop. The same rule as Force Stop decides whether the
        // directory is usable; unlike Force Stop, an unusable one is not a
        // failure here, because the uninstall does not need it.
        var processesTerminated = 0;
        string stopSummary;
        if (ApplicationProcessMatcher.CanResolve(installLocation))
        {
            try
            {
                var report = await _stopper.StopAsync(applicationName, installLocation!, cancellationToken);
                processesTerminated = report.Stopped;
                stopSummary = StopSummary(report);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The stop step is preparation, not the task. Terminating a
                // protected process is denied even to LocalSystem, and letting
                // that denial escape would abort the removal before Windows
                // Installer was asked -- for a product whose own uninstaller
                // stops that process itself. So the uninstall proceeds and the
                // summary says what actually happened: some of the application
                // may still have been running when it started. Force Stop keeps
                // failing on this, because there stopping *is* the task.
                //
                // The count stays 0: the report is what carries it, and a step
                // that threw did not produce one. Claiming a number nobody
                // counted would be worse than admitting to none.
                logger.LogWarning(
                    ex,
                    "The stop step for {Application} did not complete; the removal continues.",
                    applicationName);
                stopSummary = "not fully stopped";
            }
        }
        else
        {
            stopSummary = "not stopped: no install location";
        }

        // Step two: uninstall.
        var outcome = method == WindowsInstallerMethod
            ? await remover.RemoveWindowsInstallerProductAsync(identity, cancellationToken)
            : await remover.RemovePackageAsync(identity, cancellationToken);

        var resultJson = ResultJson(method, processesTerminated, outcome);

        if (!outcome.Succeeded)
        {
            logger.LogWarning(
                "Application {Application} was not removed ({Result}): {Detail}",
                applicationName, outcome.Result, outcome.Detail);
            return new AgentTaskResult(
                false, $"'{applicationName}' could not be removed: {Reason(outcome)}.", resultJson);
        }

        if (outcome.Result == ApplicationRemovalResult.AlreadyRemoved)
        {
            // Not a failure. The operator wanted it gone, and it is gone.
            logger.LogInformation("Application {Application} was already removed.", applicationName);
            return new AgentTaskResult(true, $"'{applicationName}' was already removed.", resultJson);
        }

        logger.LogWarning(
            "Application {Application}: {Stopped} process(es) terminated; removed by an authorized task.",
            applicationName, processesTerminated);

        var message = outcome.Result == ApplicationRemovalResult.RemovedRebootRequired
            ? $"'{applicationName}' removed: {stopSummary}; a restart is required to finish."
            : $"'{applicationName}' removed: {stopSummary}; uninstalled.";
        return new AgentTaskResult(true, message, resultJson);
    }

    /// <summary>
    /// What the stop step actually achieved, drawing the same three distinctions
    /// <see cref="StopApplicationExecutor"/> draws.
    /// </summary>
    /// <remarks>
    /// "Not running" must mean nothing matched. Saying it when processes did
    /// match and none could be terminated reports a stop that never happened, on
    /// the one path where the application is about to be uninstalled out from
    /// under those processes -- and it is the exact drift between Force Stop and
    /// Remove that sharing <see cref="ApplicationStopper"/> exists to prevent.
    /// Unlike Force Stop this is still not a failure: the uninstall is the task,
    /// and Windows Installer gives the verdict. It just has to be said honestly.
    /// </remarks>
    private static string StopSummary(ApplicationStopReport report)
    {
        if (report.WasNotRunning)
        {
            return "not running";
        }

        if (report.Stopped == 0)
        {
            return $"{report.Matched} process(es) could not be stopped";
        }

        return report.NotTerminated.Count > 0
            ? $"{report.Stopped} process(es) terminated, {report.NotTerminated.Count} could not be"
            : $"{report.Stopped} process(es) terminated";
    }

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The remover's reason, as a clause: no trailing period, never empty.</summary>
    private static string Reason(ApplicationRemovalOutcome outcome)
    {
        var detail = outcome.Detail?.Trim().TrimEnd('.');
        return string.IsNullOrEmpty(detail) ? $"the remover reported {outcome.Result}" : detail;
    }

    private static string ResultJson(string method, int processesTerminated, ApplicationRemovalOutcome outcome) =>
        JsonSerializer.Serialize(new
        {
            method,
            processesTerminated,
            result = outcome.Result.ToString(),
            code = outcome.Code,
        });
}
