using System.Security.Cryptography;
using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Installs an approved software package for an <c>InstallPackage</c> task.
/// </summary>
/// <remarks>
/// <para>
/// The pull-and-verify sequence is the whole point: the agent downloads the
/// package to a private temp file, verifies the SHA-256 against the pin in the
/// task payload, and only then hands the file to <see cref="IPackageInstaller"/>,
/// which independently verifies the Authenticode signer before installing. A
/// server that was tricked into serving the wrong bytes, or a network tamperer,
/// cannot get anything installed: the hash check fails first, the signature check
/// fails second.
/// </para>
/// <para>
/// Installs are idempotent by ProductCode: a package already present is reported
/// as success without reinstalling, and success is confirmed by re-checking the
/// ProductCode afterwards.
/// </para>
/// </remarks>
public sealed class InstallPackageExecutor(
    IAgentApiClient apiClient,
    IDeviceCredentialStore credentialStore,
    IPackageInstaller installer,
    ILogger<InstallPackageExecutor> logger) : ITaskExecutor
{
    private readonly IAgentApiClient _apiClient = apiClient;
    private readonly IDeviceCredentialStore _credentialStore = credentialStore;
    private readonly IPackageInstaller _installer = installer;
    private readonly ILogger<InstallPackageExecutor> _logger = logger;

    public string TaskType => "InstallPackage";

    public async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(task.PayloadJson))
        {
            return new AgentTaskResult(false, "Missing install-package payload.", null);
        }

        Guid packageId;
        string expectedSha256;
        string productCode;
        string? requiredSigner;
        string packageName;
        try
        {
            using var doc = JsonDocument.Parse(task.PayloadJson);
            var root = doc.RootElement;
            packageId = root.GetProperty("packageId").GetGuid();
            expectedSha256 = (root.GetProperty("sha256").GetString() ?? "").ToLowerInvariant();
            productCode = root.GetProperty("msiProductCode").GetString() ?? "";
            requiredSigner = root.TryGetProperty("requiredSignerSubject", out var rs) ? rs.GetString() : null;
            packageName = root.TryGetProperty("packageName", out var pn) ? pn.GetString() ?? "package" : "package";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return new AgentTaskResult(false, "Malformed install-package payload.", null);
        }

        if (expectedSha256.Length != 64 || string.IsNullOrWhiteSpace(productCode))
        {
            return new AgentTaskResult(false, "Install-package payload is incomplete.", null);
        }

        var credential = await _credentialStore.LoadAsync(cancellationToken);
        if (credential is null)
        {
            return new AgentTaskResult(false, "No device credential available.", null);
        }

        // Idempotency: if the product is already installed, we are done.
        if (await _installer.IsProductInstalledAsync(productCode, cancellationToken))
        {
            _logger.LogInformation("Package {Package} ({Product}) already installed; skipping.", packageName, productCode);
            return new AgentTaskResult(true, $"'{packageName}' was already installed.", null);
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"epa-pkg-{Guid.CreateVersion7():N}.msi");
        try
        {
            var download = await DownloadAsync(packageId, tempPath, credential, cancellationToken);
            if (download is not null)
            {
                return download;
            }

            var actualSha256 = await ComputeSha256Async(tempPath, cancellationToken);
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Package {Package} content hash mismatch (expected {Expected}, got {Actual}); refusing install.",
                    packageName, expectedSha256, actualSha256);
                return new AgentTaskResult(false, "Downloaded package failed its content-hash check; not installed.", null);
            }

            var outcome = await _installer.InstallAsync(tempPath, requiredSigner, cancellationToken);

            if (outcome.Result == PackageInstallResult.SignatureRejected)
            {
                _logger.LogWarning("Package {Package} signature rejected: {Detail}", packageName, outcome.Detail);
                return new AgentTaskResult(false, $"Package signature rejected: {outcome.Detail}", null);
            }

            if (!outcome.Succeeded)
            {
                _logger.LogWarning("Package {Package} install failed: {Detail}", packageName, outcome.Detail);
                return new AgentTaskResult(
                    false, $"Install failed: {outcome.Detail}",
                    ResultJson(outcome));
            }

            // Confirm the product is present after a reported success.
            if (!await _installer.IsProductInstalledAsync(productCode, cancellationToken))
            {
                return new AgentTaskResult(
                    false, "Installer reported success but the product is not detectable afterwards.",
                    ResultJson(outcome));
            }

            var message = outcome.Result == PackageInstallResult.SucceededRebootRequired
                ? $"'{packageName}' installed; a reboot is required to complete."
                : $"'{packageName}' installed.";
            _logger.LogInformation("{Message}", message);
            return new AgentTaskResult(true, message, ResultJson(outcome));
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private async Task<AgentTaskResult?> DownloadAsync(
        Guid packageId, string tempPath, DeviceCredential credential, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var result = await _apiClient.DownloadPackageAsync(packageId, file, credential, cancellationToken);

        return result.Status switch
        {
            AgentApiStatus.Success => null,
            AgentApiStatus.Unauthorized => new AgentTaskResult(false, "Not authorized to download the package.", null),
            AgentApiStatus.Rejected => new AgentTaskResult(false, "The server refused the package download.", null),
            _ => new AgentTaskResult(false, "Could not download the package (transient).", null),
        };
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private static string ResultJson(PackageInstallOutcome outcome) =>
        JsonSerializer.Serialize(new
        {
            result = outcome.Result.ToString(),
            installerExitCode = outcome.InstallerExitCode,
        });

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not delete temp package file {Path}.", path);
        }
    }
}
