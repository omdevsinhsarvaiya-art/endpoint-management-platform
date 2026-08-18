using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Software;

/// <summary>
/// The kind of installer a package carries. Deliberately closed to Windows
/// Installer (MSI): the agent installs an MSI through the Windows Installer
/// service API, never a shell, an arbitrary executable, or a script (ADR-0005).
/// Widening this enum is an explicit, reviewed decision - a new member without a
/// matching, signature-verified installer path must not compile away the
/// guarantee.
/// </summary>
public enum SoftwarePackageType
{
    WindowsInstaller = 0,
}

/// <summary>
/// An approved, content-addressed software package that may be deployed to
/// devices. The row is metadata only; the installer bytes live in the package
/// content store, addressed by <see cref="Sha256"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two independent pins gate every install, both enforced on the agent before a
/// single byte is handed to the installer:
/// </para>
/// <list type="bullet">
///   <item><see cref="Sha256"/> - the exact content. A substituted payload fails
///   the hash check and is never installed.</item>
///   <item><see cref="RequiredSignerSubject"/> - the Authenticode signer. Even
///   the exact pinned bytes are only installed if they carry a trusted signature
///   whose subject matches. A package with no required signer is accepted but
///   flagged; operators are expected to pin one for anything they did not build.</item>
/// </list>
/// <para>
/// <see cref="MsiProductCode"/> is the installer's own product identity. It backs
/// idempotent installs (already-present is a success, not a re-install) and
/// post-install verification (the product must be present afterwards).
/// </para>
/// </remarks>
public sealed class SoftwarePackage : AuditableEntity
{
    private SoftwarePackage()
    {
    }

    public SoftwarePackage(
        Guid organizationId,
        string name,
        string version,
        string? publisher,
        SoftwarePackageType type,
        string sha256,
        string fileName,
        long sizeBytes,
        string msiProductCode,
        string? requiredSignerSubject,
        Guid createdByUserId,
        string createdByDisplay)
    {
        OrganizationId = Guard.NotEmpty(organizationId, nameof(organizationId));
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name), maxLength: 256);
        Version = Guard.NotNullOrWhiteSpace(version, nameof(version), maxLength: 64);
        Publisher = Guard.OptionalMaxLength(publisher, 256, nameof(publisher));
        Type = type;
        Sha256 = ValidateSha256(sha256);
        FileName = ValidateFileName(fileName);
        SizeBytes = sizeBytes > 0
            ? sizeBytes
            : throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Package size must be positive.");
        MsiProductCode = ValidateProductCode(msiProductCode);
        RequiredSignerSubject = Guard.OptionalMaxLength(requiredSignerSubject, 512, nameof(requiredSignerSubject));
        CreatedByUserId = Guard.NotEmpty(createdByUserId, nameof(createdByUserId));
        CreatedByDisplay = Guard.NotNullOrWhiteSpace(createdByDisplay, nameof(createdByDisplay), 256);
    }

    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; } = null!;

    public string Version { get; private set; } = null!;

    public string? Publisher { get; private set; }

    public SoftwarePackageType Type { get; private set; }

    /// <summary>Lowercase hex SHA-256 of the installer content.</summary>
    public string Sha256 { get; private set; } = null!;

    public string FileName { get; private set; } = null!;

    public long SizeBytes { get; private set; }

    /// <summary>MSI ProductCode GUID in registry form, e.g. <c>{0000...}</c>, uppercase.</summary>
    public string MsiProductCode { get; private set; } = null!;

    /// <summary>
    /// Substring the Authenticode signer subject must contain (case-insensitive),
    /// or null to accept any trusted signature. Never a way to accept an unsigned file.
    /// </summary>
    public string? RequiredSignerSubject { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public string CreatedByDisplay { get; private set; } = null!;

    public bool IsWithdrawn { get; private set; }

    public DateTimeOffset? WithdrawnAt { get; private set; }

    /// <summary>Withdraw the package so it can no longer be deployed or downloaded.</summary>
    public void Withdraw(DateTimeOffset now)
    {
        if (IsWithdrawn)
        {
            return;
        }

        IsWithdrawn = true;
        WithdrawnAt = now;
    }

    private static string ValidateSha256(string value)
    {
        var v = Guard.NotNullOrWhiteSpace(value, nameof(value), maxLength: 64).ToLowerInvariant();
        if (v.Length != 64 || !v.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("SHA-256 must be exactly 64 hex characters.", nameof(value));
        }

        return v;
    }

    private static string ValidateFileName(string value)
    {
        var v = Guard.NotNullOrWhiteSpace(value, nameof(value), maxLength: 256);
        if (v.IndexOfAny(['/', '\\']) >= 0 || v.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("File name must not contain path separators.", nameof(value));
        }

        if (!v.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only .msi packages are supported.", nameof(value));
        }

        return v;
    }

    private static string ValidateProductCode(string value)
    {
        var v = Guard.NotNullOrWhiteSpace(value, nameof(value), maxLength: 38).Trim().ToUpperInvariant();
        if (!Guid.TryParse(v, out var parsed))
        {
            throw new ArgumentException("MSI product code must be a GUID.", nameof(value));
        }

        // Canonical registry form: braced, uppercase.
        return parsed.ToString("B").ToUpperInvariant();
    }
}
