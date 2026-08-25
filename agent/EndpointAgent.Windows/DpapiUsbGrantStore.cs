using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using EndpointAgent.Core;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Windows;

/// <summary>
/// Keeps the current USB grant set across service restarts and reboots,
/// DPAPI-protected at LocalMachine scope.
/// </summary>
/// <remarks>
/// <para>
/// Persistence is what lets a machine boot with a stick already in the port and
/// know immediately whether it is allowed, without waiting to reach the server.
/// It is also what lets a grant expire on time on a laptop that never comes
/// back online: the deadline travels with the grant, and the agent enforces it
/// against its own clock.
/// </para>
/// <para>
/// Every failure yields <see cref="UsbGrantSet.Empty"/> — missing file, damaged
/// file, file sealed on another machine, malformed contents. Empty means "no
/// grants", which means every storage device is restricted. There is no path
/// through this class where a problem results in more access.
/// </para>
/// <para>
/// <b>What this protects against, and what it does not.</b> LocalMachine DPAPI
/// plus the state directory's ACL (SYSTEM and Administrators only) means a
/// standard user can neither read the grant set nor forge one, and a copy taken
/// off the machine is inert. It is not a defence against a local administrator,
/// who can stop the service outright — no user-mode agent can be. The
/// meaningful guarantee is that the ordinary user this control exists for
/// cannot grant themselves access.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiUsbGrantStore : IUsbGrantStore
{
    private const string StateFileName = "usb-grants.bin";

    /// <summary>
    /// Distinct from the credential and enrollment entropies, so a blob from one
    /// store cannot be unsealed as another even though all are LocalMachine-scoped
    /// on this host.
    /// </summary>
    private static readonly byte[] Entropy = "EndpointPlatformAgent.UsbGrants.v1"u8.ToArray();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _stateDirectory;
    private readonly ILogger<DpapiUsbGrantStore> _logger;

    public DpapiUsbGrantStore(IOptions<AgentOptions> options, ILogger<DpapiUsbGrantStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stateDirectory = options.Value.StateDirectory ?? AgentPaths.StateDirectory;
    }

    private string StatePath => Path.Combine(_stateDirectory, StateFileName);

    public async ValueTask<UsbGrantSet> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return UsbGrantSet.Empty;
            }

            var protectedBytes = await File.ReadAllBytesAsync(StatePath, cancellationToken);

            byte[] plainBytes;
            try
            {
                plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
            }
            catch (CryptographicException)
            {
                _logger.LogWarning(
                    "The stored USB grant set could not be unsealed on this machine. Treating every USB "
                    + "storage device as restricted.");
                return UsbGrantSet.Empty;
            }

            try
            {
                return JsonSerializer.Deserialize<UsbGrantSet>(plainBytes, Json) ?? UsbGrantSet.Empty;
            }
            catch (JsonException)
            {
                _logger.LogWarning(
                    "The stored USB grant set is malformed. Treating every USB storage device as restricted.");
                return UsbGrantSet.Empty;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(
                ex, "Could not read the stored USB grant set. Treating every USB storage device as restricted.");
            return UsbGrantSet.Empty;
        }
    }

    public async ValueTask SaveAsync(UsbGrantSet grants, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grants);

        // The MSI creates this directory, but a developer run or a repaired
        // install may not have; creating it here keeps the first save from
        // failing on a machine that is otherwise fine.
        Directory.CreateDirectory(_stateDirectory);

        var plainBytes = JsonSerializer.SerializeToUtf8Bytes(grants, Json);
        var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.LocalMachine);

        // Write-then-move: a crash mid-write cannot leave a half-written file
        // that would read as corrupt — and therefore as "no grants" — on the
        // next start. Failing safe is right for a damaged file, but not
        // something to invite through a non-atomic write.
        var temporaryPath = StatePath + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken);
        File.Move(temporaryPath, StatePath, overwrite: true);
    }
}
