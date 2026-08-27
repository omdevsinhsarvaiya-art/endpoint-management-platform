using System.Runtime.Versioning;
using System.Text.Json;
using EndpointAgent.Core;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Windows;

/// <summary>
/// The record of which accounts this agent has elevated, as plain JSON in the
/// agent's state directory.
/// </summary>
/// <remarks>
/// Deliberately the least clever file the agent writes, for the same reason as
/// the USB restriction ledger: it has to be readable at the worst possible
/// moment. A sealed file that cannot be decrypted after a re-image would leave
/// elevated accounts with no record of which ones are ours to lower.
/// See <see cref="IElevationLedger"/> for why tampering with it cannot widen
/// access.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class FileElevationLedger : IElevationLedger
{
    /// <summary>Referenced by operators during recovery. Do not rename lightly.</summary>
    public const string StateFileName = "elevated-accounts.json";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _stateDirectory;
    private readonly ILogger<FileElevationLedger> _logger;

    public FileElevationLedger(IOptions<AgentOptions> options, ILogger<FileElevationLedger> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stateDirectory = options.Value.StateDirectory ?? AgentPaths.StateDirectory;
    }

    private string StatePath => Path.Combine(_stateDirectory, StateFileName);

    public async ValueTask<IReadOnlyCollection<string>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<string[]>(
                await File.ReadAllBytesAsync(StatePath, cancellationToken), Json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Empty understates the truth if the file was damaged, so it is loud:
            // the visible symptom would be an account that stays elevated with
            // nothing explaining why.
            _logger.LogError(
                ex, "Could not read {Path}. An account elevated by a previous run may need to be "
                + "returned to standard by hand.", StatePath);

            return [];
        }
    }

    public async ValueTask SaveAsync(
        IReadOnlyCollection<string> sids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sids);

        Directory.CreateDirectory(_stateDirectory);

        // Write-then-move: a torn file here reads as "nothing to lower".
        var temporaryPath = StatePath + ".tmp";
        await File.WriteAllBytesAsync(
            temporaryPath, JsonSerializer.SerializeToUtf8Bytes(sids, Json), cancellationToken);
        File.Move(temporaryPath, StatePath, overwrite: true);
    }
}
