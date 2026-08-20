using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using EndpointAgent.Core;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Configuration;
using EndpointAgent.Core.Enrollment;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Windows;

/// <summary>
/// Keeps the in-flight enrollment request across service restarts and reboots,
/// DPAPI-protected at LocalMachine scope.
/// </summary>
/// <remarks>
/// <para>
/// Same protection as the device credential and the same hardened directory, because
/// <see cref="PendingEnrollmentState.RequestSecret"/> is what redeems an approved
/// enrollment. It is a separate file rather than a field on the credential record
/// because the two never coexist: a machine is either waiting to enrol or enrolled,
/// and letting one file mean both invites one write to destroy the other.
/// </para>
/// <para>
/// LocalMachine scope, not CurrentUser: the service runs as LocalSystem and must read
/// this after a reboot with nobody logged in. The file is readable only by SYSTEM and
/// Administrators through the directory ACL, and is additionally DPAPI-sealed so a
/// copy taken off the machine is inert.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiEnrollmentStateStore : IEnrollmentStateStore
{
    private const string StateFileName = "enrollment-state.bin";

    /// <summary>
    /// Distinct from the credential store's entropy, so a blob from one cannot be
    /// unsealed as the other even though both are LocalMachine-scoped on this host.
    /// </summary>
    private static readonly byte[] Entropy = "EndpointPlatformAgent.PendingEnrollment.v1"u8.ToArray();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _stateDirectory;
    private readonly ILogger<DpapiEnrollmentStateStore> _logger;

    public DpapiEnrollmentStateStore(
        IOptions<AgentOptions> options, ILogger<DpapiEnrollmentStateStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stateDirectory = options.Value.StateDirectory ?? AgentPaths.StateDirectory;
    }

    private string StatePath => Path.Combine(_stateDirectory, StateFileName);

    public async ValueTask<PendingEnrollmentState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return null;
            }

            var protectedBytes = await File.ReadAllBytesAsync(StatePath, cancellationToken);
            byte[] plainBytes;

            try
            {
                plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
            }
            catch (CryptographicException)
            {
                // Sealed by a different machine, or the file is damaged. Either way it
                // is not a usable request. Discard it and let the agent start a fresh
                // enrollment rather than failing to start at all.
                _logger.LogWarning(
                    "The stored enrollment request could not be unsealed on this machine; discarding it.");
                await ClearAsync(cancellationToken);
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<PendingEnrollmentState>(plainBytes, Json);
            }
            catch (JsonException)
            {
                _logger.LogWarning("The stored enrollment request is malformed; discarding it.");
                await ClearAsync(cancellationToken);
                return null;
            }
            finally
            {
                // The secret was in this buffer; do not leave it for the GC to move around.
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Could not read the stored enrollment request.");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied reading the stored enrollment request.");
            return null;
        }
    }

    public async ValueTask SaveAsync(
        PendingEnrollmentState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        Directory.CreateDirectory(_stateDirectory);

        var plainBytes = JsonSerializer.SerializeToUtf8Bytes(state, Json);
        try
        {
            var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.LocalMachine);

            // Write-then-move, so a crash mid-write cannot leave a half-written state
            // file that would look like a corrupt request on the next start.
            var temporaryPath = StatePath + ".tmp";
            await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken);
            File.Move(temporaryPath, StatePath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (File.Exists(StatePath))
            {
                File.Delete(StatePath);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not remove the stored enrollment request.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied removing the stored enrollment request.");
        }

        return ValueTask.CompletedTask;
    }
}
