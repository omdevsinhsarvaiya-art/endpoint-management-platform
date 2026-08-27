using System.Security.Cryptography;
using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Drivers;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Installs an approved driver package for an <c>InstallDriverPackage</c> task.
/// </summary>
/// <remarks>
/// <para>
/// The gate order is the design. Each gate is cheap relative to the damage the next
/// one would allow, and each refuses without having touched anything more than a
/// temporary directory:
/// </para>
/// <list type="number">
///   <item><b>Freshness</b> -- a payload older than the window is refused before any
///   download. A captured task cannot be replayed into an installation months later.</item>
///   <item><b>Hardware match</b> -- no present device matching the package's hardware
///   id means nothing to install, decided before the driver store is opened.</item>
///   <item><b>Downgrade</b> -- refused unless the request explicitly authorized one.</item>
///   <item><b>Content hash</b> -- the archive must match the pin before a single
///   entry is extracted, so tampered bytes are never even unpacked.</item>
///   <item><b>Safe extraction</b> -- traversal, absolute paths, entry count and
///   expanded size, all refused.</item>
///   <item><b>Catalogue signature and signer pin</b> -- performed by the installer
///   before Windows sees the package.</item>
/// </list>
/// <para>
/// Nothing extracted is executed. The files are handed to Windows as data and Windows
/// installs the driver; this agent runs no binary from the archive and launches no
/// process at all (ADR-0005).
/// </para>
/// <para>
/// One-shot by construction. The server's task state machine admits a result only for
/// a Delivered task and every outcome here is terminal, so a replayed payload has no
/// task to attach to -- and the freshness gate refuses it independently even if one
/// somehow existed.
/// </para>
/// </remarks>
public sealed class InstallDriverPackageExecutor(
    IAgentApiClient apiClient,
    IDeviceCredentialStore credentialStore,
    IDriverInstaller installer,
    TimeProvider timeProvider,
    ILogger<InstallDriverPackageExecutor> logger) : ITaskExecutor
{
    private readonly IAgentApiClient _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));

    private readonly IDeviceCredentialStore _credentialStore = credentialStore
        ?? throw new ArgumentNullException(nameof(credentialStore));

    private readonly IDriverInstaller _installer = installer ?? throw new ArgumentNullException(nameof(installer));

    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<InstallDriverPackageExecutor> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// How old an instruction may be and still be acted on.
    /// </summary>
    /// <remarks>
    /// Deliberately shorter than the task's own time-to-live so the payload's age is
    /// an independent check rather than a restatement of task expiry. Installing
    /// kernel code on the strength of a day-old instruction is not something an
    /// operator would expect, whatever the queue did in the meantime.
    /// </remarks>
    public static readonly TimeSpan MaximumInstructionAge = TimeSpan.FromHours(6);

    /// <summary>Tolerance for the endpoint's clock running behind the server's.</summary>
    private static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromMinutes(10);

    public string TaskType => "InstallDriverPackage";

    public async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (!TryParse(task.PayloadJson, out var request, out var parseError))
        {
            return Failure(parseError!);
        }

        // ---- Gate 1: freshness -------------------------------------------
        var now = _timeProvider.GetUtcNow();
        var age = now - request!.IssuedAt;

        if (age > MaximumInstructionAge)
        {
            _logger.LogWarning(
                "Refusing driver package {Package}: issued {Age} ago, beyond the {Max} freshness window.",
                request.PackageName, age, MaximumInstructionAge);

            return Failure(
                $"This instruction was issued {age.TotalHours:F1} hours ago and is no longer fresh enough "
                + "to act on. Re-issue it if the installation is still wanted.");
        }

        if (request.IssuedAt - now > ClockSkewAllowance)
        {
            return Failure("This instruction is dated in the future; refusing to act on it.");
        }

        // ---- Gate 2: does this machine have the hardware? ----------------
        var matches = await _installer.FindMatchingInstancesAsync(request.HardwareId, cancellationToken);

        if (matches.Count == 0)
        {
            _logger.LogInformation(
                "No present device matches hardware id {HardwareId}; not installing {Package}.",
                request.HardwareId, request.PackageName);

            return new AgentTaskResult(
                false,
                $"No device on this machine matches '{request.HardwareId}'; nothing was installed.",
                Evidence(DriverInstallResult.HardwareMismatch, [], null));
        }

        // ---- Gate 3: downgrade -------------------------------------------
        if (!request.AllowDowngrade
            && request.ExpectedDriverVersion is { } target
            && FindNewer(matches, target) is { } newer)
        {
            _logger.LogWarning(
                "Refusing to install {Package}: {Instance} already runs {Installed}, newer than {Target}.",
                request.PackageName, newer.InstanceId, newer.DriverVersion, target);

            return new AgentTaskResult(
                false,
                $"'{newer.InstanceId}' already runs driver {newer.DriverVersion}, which is newer than "
                + $"{target}. Re-issue with a downgrade explicitly authorized if that is intended.",
                Evidence(DriverInstallResult.DowngradeRefused, [], null));
        }

        var credential = await _credentialStore.LoadAsync(cancellationToken);
        if (credential is null)
        {
            return Failure("No device credential available.");
        }

        var workingDirectory = Path.Combine(
            Path.GetTempPath(), $"epa-drv-{Guid.CreateVersion7():N}");
        var archivePath = Path.Combine(workingDirectory, "package.zip");
        var extractDirectory = Path.Combine(workingDirectory, "extracted");

        try
        {
            Directory.CreateDirectory(workingDirectory);

            // ---- Gate 4: content hash ------------------------------------
            if (await DownloadAsync(request.PackageId, archivePath, credential, cancellationToken)
                is { } downloadFailure)
            {
                return downloadFailure;
            }

            var actualSha256 = await ComputeSha256Async(archivePath, cancellationToken);
            if (!string.Equals(actualSha256, request.Sha256, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Driver package {Package} content hash mismatch (expected {Expected}, got {Actual}); refusing.",
                    request.PackageName, request.Sha256, actualSha256);

                return Failure("The downloaded driver package failed its content-hash check; nothing was extracted.");
            }

            // ---- Gate 5: safe extraction ---------------------------------
            var extraction = DriverArchive.Extract(archivePath, extractDirectory, request.InfFileName);

            if (!extraction.Succeeded)
            {
                _logger.LogWarning(
                    "Driver package {Package} rejected during extraction: {Result} ({Detail}).",
                    request.PackageName, extraction.Result, extraction.Detail);

                return new AgentTaskResult(
                    false,
                    $"The driver package was rejected: {extraction.Detail}",
                    JsonSerializer.Serialize(new
                    {
                        outcome = "ArchiveRejected",
                        reason = extraction.Result.ToString(),
                        entryCount = extraction.EntryCount,
                    }));
            }

            // ---- Gates 6 and 7: signature, signer pin, install, verify ----
            var outcome = await _installer.InstallAsync(
                extraction.InfPath!,
                request.HardwareId,
                request.RequiredSignerSubject,
                request.ExpectedDriverVersion,
                request.ExpectedProvider,
                cancellationToken);

            return Report(request, outcome);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Driver package {Package} could not be staged locally.", request.PackageName);
            return Failure("The driver package could not be staged locally; nothing was installed.");
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
        }
    }

    /// <summary>
    /// Turns an installer outcome into a task result, carrying per-instance evidence.
    /// </summary>
    /// <remarks>
    /// PendingReboot is reported as success with its own outcome value, never folded
    /// into Verified. The package is staged and correct; the driver is not yet active,
    /// and the evidence says so rather than letting a console infer completion.
    /// </remarks>
    private AgentTaskResult Report(InstallDriverRequest request, DriverInstallOutcome outcome)
    {
        var evidence = Evidence(outcome.Result, outcome.Instances, outcome.Detail);

        if (!outcome.Succeeded)
        {
            _logger.LogWarning(
                "Driver package {Package} did not install: {Result} ({Detail}).",
                request.PackageName, outcome.Result, outcome.Detail);

            var message = outcome.Result switch
            {
                DriverInstallResult.SignatureRejected =>
                    $"Driver package signature rejected: {outcome.Detail}",
                DriverInstallResult.SignerMismatch =>
                    "The driver package is signed, but not by the required publisher.",
                DriverInstallResult.VerificationFailed =>
                    "Windows reported success but the endpoint does not show the expected driver.",
                _ => $"Driver installation failed: {outcome.Detail}",
            };

            return new AgentTaskResult(false, message, evidence);
        }

        var verified = outcome.Instances.Count(i => i.Verified);

        var summary = outcome.Result == DriverInstallResult.PendingReboot
            ? $"'{request.PackageName}' was installed on {outcome.Instances.Count} device(s); "
              + "a restart is required before the driver becomes active."
            : $"'{request.PackageName}' was installed and verified on {verified} device(s).";

        _logger.LogInformation("{Summary}", summary);
        return new AgentTaskResult(true, summary, evidence);
    }

    /// <summary>
    /// The result document: the outcome plus what each affected instance looks like.
    /// </summary>
    /// <remarks>
    /// Per-instance rather than aggregated, because a hardware id can match several
    /// devices and one succeeding does not make the others fine. Nothing here is a
    /// secret: instance ids, versions and problem codes.
    /// </remarks>
    private static string Evidence(
        DriverInstallResult result, IReadOnlyList<DriverInstanceVerification> instances, string? detail) =>
        JsonSerializer.Serialize(new
        {
            outcome = result.ToString(),
            detail,
            instanceCount = instances.Count,
            verifiedCount = instances.Count(i => i.Verified),
            instances = instances.Select(i => new
            {
                instanceId = i.InstanceId,
                verified = i.Verified,
                observedVersion = i.ObservedVersion,
                observedProvider = i.ObservedProvider,
                observedInf = i.ObservedInf,
                problemCode = i.ProblemCode,
                detail = i.Detail,
            }),
        });

    /// <summary>
    /// The first matching instance running a driver strictly newer than the package.
    /// </summary>
    /// <remarks>
    /// A version that will not parse is not treated as newer. Refusing on an
    /// unparseable string would block legitimate installs over vendor version schemes
    /// that are not dotted numbers, and the downgrade guard exists to catch the clear
    /// case rather than every ambiguous one.
    /// </remarks>
    private static (string InstanceId, string? DriverVersion)? FindNewer(
        IReadOnlyList<(string InstanceId, string? DriverVersion)> matches, string targetVersion)
    {
        if (!Version.TryParse(targetVersion, out var target))
        {
            return null;
        }

        foreach (var match in matches)
        {
            if (Version.TryParse(match.DriverVersion, out var installed) && installed > target)
            {
                return match;
            }
        }

        return null;
    }

    private async Task<AgentTaskResult?> DownloadAsync(
        Guid packageId, string archivePath, DeviceCredential credential, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

        var result = await _apiClient.DownloadDriverPackageAsync(packageId, file, credential, cancellationToken);

        return result.Status switch
        {
            AgentApiStatus.Success => null,
            AgentApiStatus.Unauthorized => Failure("Not authorized to download the driver package."),
            AgentApiStatus.Rejected => Failure("The server refused the driver package download."),
            _ => Failure("Could not download the driver package (transient)."),
        };
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);

        using var sha = SHA256.Create();
        return Convert.ToHexStringLower(await sha.ComputeHashAsync(stream, cancellationToken));
    }

    private static AgentTaskResult Failure(string message) => new(false, message, null);

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not remove the driver staging directory {Path}.", path);
        }
    }

    private sealed record InstallDriverRequest(
        Guid PackageId,
        string Sha256,
        string InfFileName,
        string HardwareId,
        string RequiredSignerSubject,
        string? ExpectedProvider,
        string? ExpectedDriverVersion,
        bool AllowDowngrade,
        string PackageName,
        DateTimeOffset IssuedAt);

    /// <summary>
    /// Parses the payload, refusing anything incomplete.
    /// </summary>
    /// <remarks>
    /// The signer pin is required here as well as server-side. A payload without one
    /// is malformed rather than a permissive install: the endpoint must never fall
    /// back to accepting any trusted signature because a field was missing.
    /// </remarks>
    private static bool TryParse(string? payloadJson, out InstallDriverRequest? request, out string? error)
    {
        request = null;
        error = null;

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            error = "Missing driver-package payload.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;

            string? Optional(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;

            var parsed = new InstallDriverRequest(
                root.GetProperty("packageId").GetGuid(),
                (root.GetProperty("sha256").GetString() ?? "").Trim().ToLowerInvariant(),
                root.GetProperty("infFileName").GetString() ?? "",
                root.GetProperty("hardwareId").GetString() ?? "",
                root.GetProperty("requiredSignerSubject").GetString() ?? "",
                Optional("expectedProvider"),
                Optional("expectedDriverVersion"),
                root.TryGetProperty("allowDowngrade", out var allow) && allow.GetBoolean(),
                Optional("packageName") ?? "driver package",
                root.GetProperty("issuedAt").GetDateTimeOffset());

            if (parsed.Sha256.Length != 64
                || string.IsNullOrWhiteSpace(parsed.InfFileName)
                || string.IsNullOrWhiteSpace(parsed.HardwareId)
                || string.IsNullOrWhiteSpace(parsed.RequiredSignerSubject))
            {
                error = "The driver-package payload is incomplete.";
                return false;
            }

            request = parsed;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException
                                       or InvalidOperationException or FormatException)
        {
            error = "Malformed driver-package payload.";
            return false;
        }
    }
}
