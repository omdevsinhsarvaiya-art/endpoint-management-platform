using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Agents;

public enum AgentReleaseStatus
{
    /// <summary>Uploaded and verifiable, but not yet offered to anything.</summary>
    Draft = 0,

    /// <summary>Offered for download and for agent self-update.</summary>
    Published = 1,

    /// <summary>Withdrawn. Nothing may download or install it any more.</summary>
    Revoked = 2,
}

/// <summary>
/// One distributable build of the Windows agent.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a <c>SoftwarePackage</c>. Packages are per-organization,
/// deployed to chosen devices through the in-process installer, and identified
/// by MSI ProductCode. An agent release is platform infrastructure: global, not
/// tenant-scoped, versioned with an ordering the platform reasons about
/// ("is this device outdated?"), and installed by a mechanism that must survive
/// the installing service being stopped mid-install — which the package path,
/// running <c>MsiInstallProduct</c> inside the agent process, structurally
/// cannot. Sharing the row type would entangle two different security models to
/// save one table.
/// </para>
/// <para>
/// The MSI bytes live in the content-addressed package store under this row's
/// SHA-256, so storage is shared even though the semantics are not.
/// </para>
/// <para>
/// Lifecycle is one-way: Draft → Published → Revoked. A revoked release cannot
/// be re-published; publish a fresh row instead, so history never mutates and an
/// audit trail entry always refers to exactly one immutable artifact.
/// </para>
/// </remarks>
public sealed class AgentRelease : AuditableEntity
{
    private AgentRelease()
    {
        Version = null!;
        Platform = null!;
        Architecture = null!;
        FileName = null!;
        Sha256 = null!;
        CreatedByDisplay = null!;
    }

    public AgentRelease(
        string version,
        string platform,
        string architecture,
        string fileName,
        string sha256,
        string? signerSubject,
        string? releaseNotes,
        long contentSizeBytes,
        Guid createdByUserId,
        string createdByDisplay)
    {
        Version = AgentVersionNumber.Normalize(version);
        Platform = Guard.NotNullOrWhiteSpace(platform, nameof(platform), maxLength: 32).ToLowerInvariant();
        Architecture = Guard.NotNullOrWhiteSpace(architecture, nameof(architecture), maxLength: 16).ToLowerInvariant();
        FileName = ValidateFileName(fileName);
        Sha256 = ValidateSha256(sha256);
        SignerSubject = Guard.OptionalMaxLength(signerSubject, 256);
        ReleaseNotes = Guard.OptionalMaxLength(releaseNotes, 4000);
        ContentSizeBytes = contentSizeBytes > 0
            ? contentSizeBytes
            : throw new ArgumentOutOfRangeException(nameof(contentSizeBytes));
        CreatedByUserId = Guard.NotEmpty(createdByUserId);
        CreatedByDisplay = Guard.NotNullOrWhiteSpace(createdByDisplay, nameof(createdByDisplay), maxLength: 320);
        Status = AgentReleaseStatus.Draft;
    }

    /// <summary>Three-part numeric version, e.g. <c>1.1.0</c>. Normalized, never padded.</summary>
    public string Version { get; private set; }

    /// <summary>Target OS, lowercase: <c>windows</c>.</summary>
    public string Platform { get; private set; }

    /// <summary>Target CPU architecture, lowercase: <c>x64</c>.</summary>
    public string Architecture { get; private set; }

    /// <summary>The MSI's download filename. Display data; identity is the hash.</summary>
    public string FileName { get; private set; }

    /// <summary>Lowercase hex SHA-256 of the MSI. Also the key into the content store.</summary>
    public string Sha256 { get; private set; }

    /// <summary>
    /// Substring the Authenticode signer subject must contain, or null for a
    /// build published without a signature. Null is a visible, deliberate
    /// statement — "this build is unsigned" — not a default the platform assumes.
    /// The agent enforces whichever the release declares.
    /// </summary>
    public string? SignerSubject { get; private set; }

    public string? ReleaseNotes { get; private set; }

    public long ContentSizeBytes { get; private set; }

    public AgentReleaseStatus Status { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public string CreatedByDisplay { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsPublished => Status == AgentReleaseStatus.Published;

    /// <summary>Draft → Published. Only drafts publish; a revoked build stays revoked.</summary>
    public void Publish(DateTimeOffset now)
    {
        if (Status != AgentReleaseStatus.Draft)
        {
            throw new InvalidOperationException(
                $"Release {Version} is {Status}; only a Draft release can be published.");
        }

        Status = AgentReleaseStatus.Published;
        PublishedAt = now;
    }

    /// <summary>Published (or Draft) → Revoked. Terminal.</summary>
    public void Revoke(DateTimeOffset now)
    {
        if (Status == AgentReleaseStatus.Revoked)
        {
            return; // Idempotent: revoking twice changes nothing.
        }

        Status = AgentReleaseStatus.Revoked;
        RevokedAt = now;
    }

    private static string ValidateSha256(string sha256)
    {
        var value = Guard.NotNullOrWhiteSpace(sha256, nameof(sha256), maxLength: 64).ToLowerInvariant();
        if (value.Length != 64 || !value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("SHA-256 must be 64 hex characters.", nameof(sha256));
        }

        return value;
    }

    private static string ValidateFileName(string fileName)
    {
        var value = Guard.NotNullOrWhiteSpace(fileName, nameof(fileName), maxLength: 128);
        // Served in a Content-Disposition header and written to disk by agents:
        // a path separator or traversal here must be unrepresentable.
        if (value.IndexOfAny(['/', '\\', ':', '"', '\r', '\n']) >= 0 || value.Contains(".."))
        {
            throw new ArgumentException("File name must be a bare file name.", nameof(fileName));
        }

        return value;
    }
}
