using EndpointPlatform.Domain.Agents;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Software;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Agents;

public enum AgentReleaseActionResult
{
    Success,
    NotFound,
    /// <summary>The release is not in a state that allows the action.</summary>
    Conflict,
    /// <summary>A release for this platform/architecture/version already exists.</summary>
    Duplicate,
    /// <summary>
    /// The artifact failed verification under the configured trust mode -- missing,
    /// not an MSI, bytes no longer matching the recorded hash, or (Public mode
    /// only) an unmet Authenticode requirement -- and may not be published.
    /// </summary>
    NotVerified,
}

/// <summary>A lifecycle action's result, with the reason when it was refused.</summary>
/// <param name="Reason">
/// For <see cref="AgentReleaseActionResult.NotVerified"/>, the verifier's own
/// description of the failed check -- safe to show an administrator, names the
/// requirement and never the bytes. Null otherwise.
/// </param>
public sealed record AgentReleaseActionOutcome(AgentReleaseActionResult Result, string? Reason)
{
    public static AgentReleaseActionOutcome Of(AgentReleaseActionResult result, string? reason = null) => new(result, reason);
}

/// <summary>
/// The lifecycle of distributable agent builds: upload as Draft, publish,
/// revoke, and serve — to the dashboard for download and to agents for
/// self-update.
/// </summary>
/// <remarks>
/// <para>
/// Content is stored through the same content-addressed store as software
/// packages, keyed by SHA-256 — the store recomputes the hash while writing and
/// refuses a mismatch, so a release row can never point at bytes that differ
/// from its recorded hash. Release semantics stay separate from packages on
/// purpose (see <see cref="AgentRelease"/>).
/// </para>
/// <para>
/// Agent releases are platform-global, not per-organization: every tenant's
/// devices run the same agent, and "which build is current" is a fact about the
/// platform. Audit entries are staged under the acting administrator's
/// organization, which keeps the trail queryable where the administrator lives.
/// </para>
/// </remarks>
public sealed class AgentReleaseService(
    EndpointPlatformDbContext dbContext,
    IPackageContentStore contentStore,
    IReleasePublishVerifier publishVerifier,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<AgentReleaseService> logger)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly IPackageContentStore _contentStore = contentStore;
    private readonly IReleasePublishVerifier _publishVerifier = publishVerifier;
    private readonly AuditWriter _auditWriter = auditWriter;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<AgentReleaseService> _logger = logger;

    public const string WindowsPlatform = "windows";
    public const string X64Architecture = "x64";

    /// <summary>
    /// Stores the MSI bytes and creates a Draft release. The caller's SHA-256 is
    /// verified against the actual bytes by the content store; a lie about the
    /// hash discards the upload.
    /// </summary>
    /// <param name="declaredSha256">
    /// Optional. What the uploader believes the hash is. Used only as an integrity
    /// cross-check against what the server computes -- a mismatch means the bytes
    /// were damaged in transit and the upload is refused. It is never what gets
    /// stored; the server's own hash is.
    /// </param>
    public async Task<(AgentRelease? Release, string? Error)> CreateAsync(
        string version,
        string fileName,
        string? declaredSha256,
        string? releaseNotes,
        Stream content,
        Guid actorId,
        string actorDisplay,
        Guid actorOrganizationId,
        CancellationToken cancellationToken = default)
    {
        string normalizedVersion;
        try
        {
            normalizedVersion = AgentVersionNumber.Normalize(version);
        }
        catch (ArgumentException ex)
        {
            return (null, ex.Message);
        }

        var exists = await _dbContext.AgentReleases.AnyAsync(
            r => r.Platform == WindowsPlatform && r.Architecture == X64Architecture && r.Version == normalizedVersion,
            cancellationToken);
        if (exists)
        {
            return (null, $"A windows/x64 release with version {normalizedVersion} already exists.");
        }

        // The server hashes the bytes it stores. The uploader's figure, if any, is
        // a cross-check for transit damage and nothing more.
        var (sha256, size) = await _contentStore.SaveComputingHashAsync(content, cancellationToken);

        if (!string.IsNullOrWhiteSpace(declaredSha256)
            && !string.Equals(declaredSha256.Trim(), sha256, StringComparison.OrdinalIgnoreCase))
        {
            return (null, "The uploaded bytes do not hash to the declared SHA-256; the upload was refused.");
        }

        AgentRelease release;
        try
        {
            release = new AgentRelease(
                normalizedVersion, WindowsPlatform, X64Architecture, fileName, sha256,
                signerSubject: null, releaseNotes, size, actorId, actorDisplay);
        }
        catch (ArgumentException ex)
        {
            return (null, ex.Message);
        }

        // The signer is a fact read from the artifact and recorded only when the
        // trust mode verified one. In Internal mode that is never: the signature is
        // not consulted, so nothing is recorded. Registering is not the consequential
        // act either way; publishing is, and publishing re-verifies.
        var verification = await VerifyStoredAsync(release, cancellationToken);
        release.RecordVerifiedSigner(verification.SignerSubject);

        _dbContext.AgentReleases.Add(release);

        _auditWriter.Stage(
            actorOrganizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "agent_release.created", AuditResult.Success,
            audit => audit
                .OnTarget("agent_release", release.Id.ToString(), $"windows/x64 {release.Version}")
                .Requiring(Permissions.Software.Deploy)
                .WithStateChange(null, System.Text.Json.JsonSerializer.Serialize(new
                {
                    version = release.Version,
                    fileName = release.FileName,
                    sha256 = release.Sha256,
                    signerSubject = release.SignerSubject,
                    sizeBytes = release.ContentSizeBytes,
                })));

        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Agent release {Version} created as draft by {Actor}.", release.Version, actorDisplay);
        return (release, null);
    }

    public Task<AgentReleaseActionOutcome> PublishAsync(
        Guid releaseId, Guid actorId, string actorDisplay, Guid actorOrganizationId,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(releaseId, actorId, actorDisplay, actorOrganizationId, publish: true, cancellationToken);

    public Task<AgentReleaseActionOutcome> RevokeAsync(
        Guid releaseId, Guid actorId, string actorDisplay, Guid actorOrganizationId,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(releaseId, actorId, actorDisplay, actorOrganizationId, publish: false, cancellationToken);

    private async Task<AgentReleaseActionOutcome> TransitionAsync(
        Guid releaseId, Guid actorId, string actorDisplay, Guid actorOrganizationId, bool publish,
        CancellationToken cancellationToken)
    {
        var release = await _dbContext.AgentReleases
            .SingleOrDefaultAsync(r => r.Id == releaseId, cancellationToken);
        if (release is null)
        {
            return AgentReleaseActionOutcome.Of(AgentReleaseActionResult.NotFound);
        }

        var before = release.Status.ToString();
        var now = _timeProvider.GetUtcNow();

        if (publish && release.Status == AgentReleaseStatus.Draft)
        {
            // The gate. Re-verified now rather than trusted from upload time, because
            // the bytes on disk can have changed since. What is published is what is
            // checked: existence, MSI shape, and the stored-byte hash in every mode;
            // Authenticode only where the trust mode calls for it.
            var verification = await VerifyStoredAsync(release, cancellationToken);
            if (!verification.IsTrusted)
            {
                _logger.LogWarning(
                    "Refusing to publish agent release {Version} ({Mode}): {Reason}",
                    release.Version, verification.Mode, verification.Describe());
                return AgentReleaseActionOutcome.Of(AgentReleaseActionResult.NotVerified, verification.Describe());
            }

            // Keep the recorded signer equal to what was just verified.
            if (!string.Equals(release.SignerSubject, verification.SignerSubject, StringComparison.Ordinal))
            {
                release.RecordVerifiedSigner(verification.SignerSubject);
            }
        }

        try
        {
            if (publish)
            {
                release.Publish(now);
            }
            else
            {
                release.Revoke(now);
            }
        }
        catch (InvalidOperationException)
        {
            return AgentReleaseActionOutcome.Of(AgentReleaseActionResult.Conflict);
        }

        _auditWriter.Stage(
            actorOrganizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: publish ? "agent_release.published" : "agent_release.revoked",
            AuditResult.Success,
            audit => audit
                .OnTarget("agent_release", release.Id.ToString(), $"windows/x64 {release.Version}")
                .Requiring(Permissions.Software.Deploy)
                .WithStateChange(
                    System.Text.Json.JsonSerializer.Serialize(new { status = before }),
                    System.Text.Json.JsonSerializer.Serialize(new { status = release.Status.ToString() })));

        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Agent release {Version} {Action} by {Actor}.", release.Version, publish ? "published" : "revoked", actorDisplay);
        return AgentReleaseActionOutcome.Of(AgentReleaseActionResult.Success);
    }

    /// <summary>The newest published windows/x64 release, or null when none is.</summary>
    /// <remarks>
    /// Version order is numeric, decided in memory: the version column is a
    /// string, and letting SQL order it would reintroduce exactly the
    /// 1.0.9-beats-1.0.10 bug the version type exists to prevent. Published
    /// releases number a handful, so materialising them costs nothing.
    /// </remarks>
    public async Task<AgentRelease?> GetLatestPublishedAsync(CancellationToken cancellationToken = default)
    {
        var published = await _dbContext.AgentReleases
            .AsNoTracking()
            .Where(r =>
                r.Platform == WindowsPlatform
                && r.Architecture == X64Architecture
                && r.Status == AgentReleaseStatus.Published)
            .ToListAsync(cancellationToken);

        return published
            .Where(r => AgentVersionNumber.TryParse(r.Version, out _))
            .OrderByDescending(r =>
            {
                AgentVersionNumber.TryParse(r.Version, out var v);
                return v;
            })
            .FirstOrDefault();
    }

    /// <summary>
    /// Everything a release must satisfy before it may be published, under the
    /// configured trust mode.
    /// </summary>
    /// <remarks>
    /// Reads the bytes from the content store and hands them to
    /// <see cref="IReleasePublishVerifier"/>, which owns the rules. The service
    /// contributes only the recorded hash to compare against; it does not decide
    /// what counts as trusted.
    /// </remarks>
    public async Task<ReleaseVerification> VerifyStoredAsync(
        AgentRelease release, CancellationToken cancellationToken = default)
    {
        await using var stream = await _contentStore.OpenReadAsync(release.Sha256, cancellationToken);
        if (stream is null)
        {
            return _publishVerifier.Verify(null, release.Sha256);
        }

        using var buffer = new MemoryStream(capacity: (int)Math.Min(release.ContentSizeBytes, int.MaxValue));
        await stream.CopyToAsync(buffer, cancellationToken);

        return _publishVerifier.Verify(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), release.Sha256);
    }

    /// <summary>
    /// Replaces a draft's artifact with new bytes -- the same build, signed.
    /// </summary>
    /// <remarks>
    /// The route by which an existing draft becomes its signed self without a
    /// second release row. The hash is recomputed by the server over the new bytes,
    /// because signing changes them, and the signer is re-derived from the new
    /// signature. Draft only; the domain enforces that.
    /// </remarks>
    public async Task<(AgentRelease? Release, string? Error)> ReplaceArtifactAsync(
        Guid releaseId,
        string fileName,
        string? declaredSha256,
        Stream content,
        Guid actorId,
        string actorDisplay,
        Guid actorOrganizationId,
        CancellationToken cancellationToken = default)
    {
        var release = await _dbContext.AgentReleases.SingleOrDefaultAsync(r => r.Id == releaseId, cancellationToken);
        if (release is null)
        {
            return (null, null);
        }

        if (release.Status != AgentReleaseStatus.Draft)
        {
            return (null, $"Release {release.Version} is {release.Status}; only a draft's artifact can be replaced.");
        }

        var (sha256, size) = await _contentStore.SaveComputingHashAsync(content, cancellationToken);

        if (!string.IsNullOrWhiteSpace(declaredSha256)
            && !string.Equals(declaredSha256.Trim(), sha256, StringComparison.OrdinalIgnoreCase))
        {
            return (null, "The uploaded bytes do not hash to the declared SHA-256; the upload was refused.");
        }

        var previousSha256 = release.Sha256;
        try
        {
            release.ReplaceArtifact(sha256, size, fileName);
        }
        catch (ArgumentException ex)
        {
            return (null, ex.Message);
        }

        var verification = await VerifyStoredAsync(release, cancellationToken);
        release.RecordVerifiedSigner(verification.SignerSubject);

        _auditWriter.Stage(
            actorOrganizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "agent_release.artifact_replaced", AuditResult.Success,
            audit => audit
                .OnTarget("agent_release", release.Id.ToString(), $"windows/x64 {release.Version}")
                .Requiring(Permissions.Software.Deploy)
                .WithStateChange(
                    System.Text.Json.JsonSerializer.Serialize(new { sha256 = previousSha256 }),
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        sha256 = release.Sha256,
                        sizeBytes = release.ContentSizeBytes,
                        signerSubject = release.SignerSubject,
                        verified = verification.IsTrusted,
                    })));

        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Agent release {Version} artifact replaced by {Actor}; signer {Signer}.",
            release.Version, actorDisplay, release.SignerSubject ?? "<unsigned>");
        return (release, null);
    }

    /// <summary>
    /// Opens a release's MSI for an administrator to download.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately separate from <see cref="OpenPublishedContentAsync"/>, and
    /// deliberately more permissive: it serves Draft as well as Published. These are
    /// two different questions. Publishing decides whether the platform will push a
    /// build onto machines by itself; downloading is an authenticated administrator
    /// fetching an artifact they uploaded, to install by hand.
    /// </para>
    /// <para>
    /// Conflating them meant a build could not be retrieved from the console until
    /// it had been made installable fleet-wide, which is exactly backwards for an
    /// unsigned build: the safe way to try one is on a single machine you are
    /// standing at, not by publishing it to every device first.
    /// </para>
    /// <para>
    /// Revoked stays refused. A revoked release is withdrawn -- "nothing may download
    /// or install it any more" is the documented lifecycle rule, and this must not
    /// become a way around it.
    /// </para>
    /// </remarks>
    public async Task<(Stream? Content, AgentRelease? Release)> OpenDownloadableContentAsync(
        Guid releaseId, CancellationToken cancellationToken = default)
    {
        var release = await _dbContext.AgentReleases
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == releaseId, cancellationToken);

        if (release is null || release.Status == AgentReleaseStatus.Revoked)
        {
            return (null, null);
        }

        var stream = await _contentStore.OpenReadAsync(release.Sha256, cancellationToken);
        return stream is null ? (null, null) : (stream, release);
    }

    /// <summary>Opens a release's MSI for streaming, refusing anything not Published.</summary>
    public async Task<(Stream? Content, AgentRelease? Release)> OpenPublishedContentAsync(
        Guid releaseId, CancellationToken cancellationToken = default)
    {
        var release = await _dbContext.AgentReleases
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == releaseId, cancellationToken);

        // Draft is not offered and Revoked is withdrawn: both answer "no such
        // downloadable release", indistinguishably from an id that never existed.
        if (release is null || !release.IsPublished)
        {
            return (null, null);
        }

        var stream = await _contentStore.OpenReadAsync(release.Sha256, cancellationToken);
        return stream is null ? (null, null) : (stream, release);
    }
}
