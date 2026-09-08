using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Inventory;

/// <summary>
/// The software collector the service runs: every discovery source, pooled,
/// through one pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Sources are independent and read-only, and one failing must cost its own
/// evidence and nothing else: a package repository that cannot be read must not
/// blank the uninstall registry's answer. Cancellation is the exception -- it is
/// the host's instruction, not a source's failure -- and is honoured rather than
/// swallowed.
/// </para>
/// <para>
/// After the sources have reported, every executable a supplementary source named
/// is asked what it says about itself -- once per distinct file, bounded, and
/// through the one abstraction that opens executables. That evidence joins the
/// pool under its own source, so the merger sees a file's product name and signer
/// as one more witness rather than as something a shortcut or alias asserted.
/// </para>
/// </remarks>
public sealed class SoftwareDiscoveryCollector(
    IEnumerable<ISoftwareEvidenceSource> sources,
    ILogger<SoftwareDiscoveryCollector> logger,
    IExecutableMetadataReader? executables = null) : ISoftwareCollector
{
    /// <summary>
    /// The most executables described in one collection. A machine referencing
    /// more than this from its shortcuts, aliases and processes has something
    /// wrong with it, and the bound keeps the cost of a hostile machine finite.
    /// </summary>
    public const int MaxExecutables = 2_000;

    private readonly IReadOnlyList<ISoftwareEvidenceSource> _sources =
        (sources ?? throw new ArgumentNullException(nameof(sources))).ToArray();

    private readonly ILogger<SoftwareDiscoveryCollector> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly IExecutableMetadataReader? _executables = executables;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<InventorySoftware>> CollectAsync(CancellationToken cancellationToken = default) =>
        SoftwareDiscoveryPipeline.Run(await CollectEvidenceAsync(cancellationToken));

    /// <summary>Everything the sources found, plus what their executables say about themselves.</summary>
    public async ValueTask<IReadOnlyList<SoftwareEvidence>> CollectEvidenceAsync(CancellationToken cancellationToken = default)
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

        if (_executables is not null)
        {
            evidence.AddRange(DescribeExecutables(evidence, cancellationToken));
        }

        return evidence;
    }

    /// <summary>One piece of evidence per distinct executable the supplementary sources named.</summary>
    private List<SoftwareEvidence> DescribeExecutables(List<SoftwareEvidence> evidence, CancellationToken cancellationToken)
    {
        var paths = evidence
            .Where(e => !e.IsAuthoritative)
            .Select(e => ExecutablePath.Normalize(e.ExecutablePath))
            .Where(p => p is not null)
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count > MaxExecutables)
        {
            _logger.LogWarning(
                "Discovery referenced {Count} distinct executables; only the first {Max} are described.",
                paths.Count, MaxExecutables);
            paths = paths.Take(MaxExecutables).ToList();
        }

        var described = new List<SoftwareEvidence>(paths.Count);

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ExecutableMetadata? metadata;
            try
            {
                metadata = _executables!.Read(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not read executable metadata for {Path}.", path);
                continue;
            }

            if (metadata is null)
            {
                continue;
            }

            described.Add(new SoftwareEvidence(
                EvidenceSource.ExecutableMetadata,
                Name: metadata.ProductName,
                Version: metadata.FileVersion,
                Publisher: metadata.CompanyName,
                ExecutablePath: path,
                SignerSubject: metadata.SignerSubject,
                SignatureStatus: metadata.SignatureStatus.ToString(),
                FileDescription: metadata.FileDescription));
        }

        _logger.LogDebug("Described {Count} of {Referenced} referenced executable(s).", described.Count, paths.Count);
        return described;
    }
}
