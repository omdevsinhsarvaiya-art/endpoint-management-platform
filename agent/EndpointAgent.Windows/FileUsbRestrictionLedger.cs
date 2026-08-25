using System.Runtime.Versioning;
using System.Text.Json;
using EndpointAgent.Core;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Windows;

/// <summary>
/// The written record of which devices this agent has disabled or marked
/// read-only, kept as plain JSON in the agent's state directory.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the least clever file the agent writes. It has to be readable
/// during uninstall, potentially long after the install that wrote it, and every
/// mechanism that could make it unreadable — DPAPI sealing, a binary format, a
/// schema that must match — trades a real risk of leaving somebody's hardware
/// disabled for a benefit this file does not need. See
/// <see cref="IUsbRestrictionLedger"/> for why tampering gains nothing.
/// </para>
/// <para>
/// The path is fixed relative to the state directory so the MSI's uninstall
/// custom action can find it without needing the agent's configuration.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class FileUsbRestrictionLedger : IUsbRestrictionLedger
{
    /// <summary>Also referenced by the uninstall custom action. Do not rename lightly.</summary>
    public const string StateFileName = "usb-restricted-devices.json";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _stateDirectory;
    private readonly ILogger<FileUsbRestrictionLedger> _logger;

    public FileUsbRestrictionLedger(IOptions<AgentOptions> options, ILogger<FileUsbRestrictionLedger> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stateDirectory = options.Value.StateDirectory ?? AgentPaths.StateDirectory;
    }

    private string StatePath => Path.Combine(_stateDirectory, StateFileName);

    public async ValueTask<IReadOnlyCollection<string>> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return [];
            }

            var bytes = await File.ReadAllBytesAsync(StatePath, cancellationToken);

            return JsonSerializer.Deserialize<string[]>(bytes, Json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Empty means "we believe we have nothing applied", which understates
            // the truth if the file was damaged. Loud, because the visible symptom
            // would be a stick that stays disabled with nothing explaining why.
            _logger.LogError(
                ex, "Could not read {Path}. Devices restricted by a previous run may need to be re-enabled "
                + "by hand if the agent is uninstalled.", StatePath);

            return [];
        }
    }

    public async ValueTask SaveAsync(
        IReadOnlyCollection<string> instanceIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);

        Directory.CreateDirectory(_stateDirectory);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(instanceIds, Json);

        // Write-then-move, for the same reason as the grant store: a torn file
        // here reads as "nothing to release".
        var temporaryPath = StatePath + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
        File.Move(temporaryPath, StatePath, overwrite: true);
    }
}
