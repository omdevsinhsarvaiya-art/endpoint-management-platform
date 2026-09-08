using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Inventory;

/// <summary>
/// The installed-software collector: every discovery source, one report.
/// </summary>
/// <remarks>
/// <para>
/// Runs each <see cref="ISoftwareEvidenceSource"/> in the order registered, pools
/// what they found, and hands the pool to <see cref="SoftwareDiscoveryPipeline"/>.
/// Sources are independent by design -- none knows another exists -- so this is
/// the only place the whole picture is assembled.
/// </para>
/// <para>
/// One source failing must not cost the inventory. A source that throws is logged
/// and its evidence omitted; the report is built from the rest. That is the same
/// rule every other collector follows, and it matters more here because several
/// of these sources read places -- package roots, shortcut files, the process
/// table -- that are routinely half-readable.
/// </para>
/// </remarks>
public sealed class SoftwareDiscoveryCollector(
    IEnumerable<ISoftwareEvidenceSource> sources,
    ILogger<SoftwareDiscoveryCollector> logger) : ISoftwareCollector
{
    private readonly IReadOnlyList<ISoftwareEvidenceSource> _sources = (sources ?? throw new ArgumentNullException(nameof(sources))).ToArray();
    private readonly ILogger<SoftwareDiscoveryCollector> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async ValueTask<IReadOnlyList<InventorySoftware>> CollectAsync(CancellationToken cancellationToken = default)
    {
        var evidence = new List<SoftwareEvidence>();

        foreach (var source in _sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var found = await source.CollectEvidenceAsync(cancellationToken);
                evidence.AddRange(found);
                _logger.LogDebug("Discovery source {Source} contributed {Count} evidence item(s).", source.SourceName, found.Count);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Discovery source {Source} failed; its evidence is omitted from this collection.", source.SourceName);
            }
        }

        return SoftwareDiscoveryPipeline.Run(evidence);
    }
}
