using EndpointAgent.Core.Inventory;

namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// One way of finding out that an application is present.
/// </summary>
/// <remarks>
/// <para>
/// <b>Observational, without exception.</b> A source reads: registry keys, package
/// manifests, shortcut files, executable metadata, the process list. No
/// implementation may execute, terminate, install, uninstall, modify or block
/// anything, and none may launch a process (ADR-0005). Discovery answers "what is
/// here"; acting on the answer is a separate, task-gated capability with its own
/// authorization and audit.
/// </para>
/// <para>
/// A source reports what it saw and nothing more -- it does not decide whether an
/// application is installed, which application its evidence belongs to, or whether
/// it duplicates another source's finding. Those are the merger's decisions, made
/// once, with everything in hand.
/// </para>
/// <para>
/// A source that cannot read what it needs returns what it managed to read rather
/// than throwing: one unavailable source must not cost the whole inventory, which
/// is the same failure rule the rest of the collectors follow.
/// </para>
/// </remarks>
public interface ISoftwareEvidenceSource
{
    /// <summary>A short, stable name for this source, for logs and diagnostics.</summary>
    string SourceName { get; }

    ValueTask<IReadOnlyList<SoftwareEvidence>> CollectEvidenceAsync(CancellationToken cancellationToken = default);
}
