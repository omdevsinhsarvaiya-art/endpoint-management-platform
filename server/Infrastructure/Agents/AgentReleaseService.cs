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
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<AgentReleaseService> logger)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly IPackageContentStore _contentStore = contentStore;
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
    public async Task<(AgentRelease? Release, string? Error)> CreateAsync(
        string version,
        string fileName,
        string sha256,
        string? signerSubject,
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

        long size;
        try
        {
            size = await _contentStore.SaveAsync(sha256, content, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // The bytes did not hash to what the caller claimed.
            return (null, ex.Message);
        }

        AgentRelease release;
        try
        {
            release = new AgentRelease(
                normalizedVersion, WindowsPlatform, X64Architecture, fileName, sha256,
                signerSubject, releaseNotes, size, actorId, actorDisplay);
        }
        catch (ArgumentException ex)
        {
            return (null, ex.Message);
        }

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

    public Task<AgentReleaseActionResult> PublishAsync(
        Guid releaseId, Guid actorId, string actorDisplay, Guid actorOrganizationId,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(releaseId, actorId, actorDisplay, actorOrganizationId, publish: true, cancellationToken);

    public Task<AgentReleaseActionResult> RevokeAsync(
        Guid releaseId, Guid actorId, string actorDisplay, Guid actorOrganizationId,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(releaseId, actorId, actorDisplay, actorOrganizationId, publish: false, cancellationToken);

    private async Task<AgentReleaseActionResult> TransitionAsync(
        Guid releaseId, Guid actorId, string actorDisplay, Guid actorOrganizationId, bool publish,
        CancellationToken cancellationToken)
    {
        var release = await _dbContext.AgentReleases
            .SingleOrDefaultAsync(r => r.Id == releaseId, cancellationToken);
        if (release is null)
        {
            return AgentReleaseActionResult.NotFound;
        }

        var before = release.Status.ToString();
        var now = _timeProvider.GetUtcNow();

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
            return AgentReleaseActionResult.Conflict;
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
        return AgentReleaseActionResult.Success;
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
