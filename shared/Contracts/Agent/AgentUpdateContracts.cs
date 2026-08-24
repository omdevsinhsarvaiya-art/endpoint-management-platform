namespace EndpointPlatform.Contracts.Agent;

/// <summary>
/// The published agent release a device may update itself to.
/// </summary>
/// <remarks>
/// This is the agent's trust anchor for updates: whatever an UpdateAgent task
/// claims, the agent installs only what this authenticated endpoint confirms —
/// same release id, same version, same SHA-256. <paramref name="SignerSubject"/>
/// null means the release was deliberately published unsigned (a documented
/// development stance), in which case the agent skips the signer-subject pin but
/// still refuses a file whose hash differs.
/// </remarks>
public sealed record AgentUpdateInfo(
    bool Available,
    Guid? ReleaseId,
    string? Version,
    string? Architecture,
    string? FileName,
    string? Sha256,
    string? SignerSubject,
    long? SizeBytes);
