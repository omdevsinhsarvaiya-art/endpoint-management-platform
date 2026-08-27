using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Drivers;

/// <summary>
/// An approved, content-addressed Windows driver package that may be installed on
/// managed endpoints.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <see cref="Software.SoftwarePackage"/> and stored the same way -- the
/// row is metadata, the bytes live in the shared content store addressed by
/// <see cref="Sha256"/> -- but deliberately a separate entity rather than a new
/// <c>SoftwarePackageType</c>. That enum documents itself as closed to MSI so the
/// agent's installer path can never be handed something else; widening it to admit
/// driver archives would compile that guarantee away.
/// </para>
/// <para>
/// A driver package is an archive, not a file, because an INF is useless without its
/// catalogue and payload. Three pins gate every installation, all enforced on the
/// endpoint before Windows sees anything:
/// </para>
/// <list type="bullet">
///   <item><see cref="Sha256"/> -- the exact archive. Checked before a single entry
///   is extracted, so tampered bytes are never even unpacked.</item>
///   <item>The catalogue signature on <see cref="InfFileName"/>, validated against
///   the trusted certificate stores.</item>
///   <item><see cref="RequiredSignerSubject"/> -- who signed it. <b>Mandatory</b>,
///   unlike the software path where a trusted signature alone suffices. A driver
///   runs in the kernel, and "signed by someone Windows trusts" is a far weaker
///   statement than "signed by the vendor we chose".</item>
/// </list>
/// <para>
/// <see cref="HardwareId"/> is what the package claims to drive. The endpoint refuses
/// to touch the driver store unless a present device actually matches it, so an
/// operator cannot push a network driver at a graphics card.
/// </para>
/// </remarks>
public sealed class DriverPackage : AuditableEntity
{
    /// <summary>
    /// Archive ceiling. Far above any real driver package -- most are single-digit
    /// megabytes -- and far below the software-package ceiling, because nothing
    /// legitimate in this category approaches it.
    /// </summary>
    public const long MaxArchiveBytes = 256L * 1024 * 1024;

    private DriverPackage()
    {
        Name = null!;
        Version = null!;
        Sha256 = null!;
        FileName = null!;
        InfFileName = null!;
        HardwareId = null!;
        RequiredSignerSubject = null!;
        CreatedByDisplay = null!;
    }

    public DriverPackage(
        Guid organizationId,
        string name,
        string version,
        string? provider,
        string sha256,
        string fileName,
        long sizeBytes,
        string infFileName,
        string hardwareId,
        string? driverVersion,
        string requiredSignerSubject,
        Guid createdByUserId,
        string createdByDisplay)
    {
        OrganizationId = Guard.NotEmpty(organizationId, nameof(organizationId));
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name), maxLength: 256);
        Version = Guard.NotNullOrWhiteSpace(version, nameof(version), maxLength: 64);
        Provider = Guard.OptionalMaxLength(provider, 256, nameof(provider));
        Sha256 = ValidateSha256(sha256);
        FileName = ValidateFileName(fileName);

        SizeBytes = sizeBytes switch
        {
            <= 0 => throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Package size must be positive."),
            > MaxArchiveBytes => throw new ArgumentOutOfRangeException(
                nameof(sizeBytes), "Driver package exceeds the maximum allowed size."),
            _ => sizeBytes,
        };

        InfFileName = ValidateInfFileName(infFileName);
        HardwareId = Guard.NotNullOrWhiteSpace(hardwareId, nameof(hardwareId), maxLength: 512);
        DriverVersion = Guard.OptionalMaxLength(driverVersion, 64, nameof(driverVersion));

        // Not optional, and not defaulted. A package with no signer pin cannot be
        // created at all, so no code path downstream has to decide what to do about
        // one.
        RequiredSignerSubject = Guard.NotNullOrWhiteSpace(
            requiredSignerSubject, nameof(requiredSignerSubject), maxLength: 512);

        CreatedByUserId = Guard.NotEmpty(createdByUserId, nameof(createdByUserId));
        CreatedByDisplay = Guard.NotNullOrWhiteSpace(createdByDisplay, nameof(createdByDisplay), 256);
    }

    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; }

    /// <summary>The package's own release label, as the administrator named it.</summary>
    public string Version { get; private set; }

    /// <summary>The publisher, e.g. "Intel". Advisory; the signer pin is the control.</summary>
    public string? Provider { get; private set; }

    /// <summary>Lowercase hex SHA-256 of the archive.</summary>
    public string Sha256 { get; private set; }

    public string FileName { get; private set; }

    public long SizeBytes { get; private set; }

    /// <summary>
    /// The INF inside the archive that installs this driver. A bare file name: it is
    /// resolved beneath the extraction directory on the endpoint and can never
    /// escape it.
    /// </summary>
    public string InfFileName { get; private set; }

    /// <summary>The PnP hardware id this package drives, e.g. <c>PCI\VEN_8086&amp;DEV_1234</c>.</summary>
    public string HardwareId { get; private set; }

    /// <summary>
    /// The driver version the endpoint must observe after installation. Null when
    /// unknown, which weakens post-install verification to identity-only and is
    /// reported as such rather than passing silently.
    /// </summary>
    public string? DriverVersion { get; private set; }

    /// <summary>Substring the catalogue signer's subject must contain. Never null.</summary>
    public string RequiredSignerSubject { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public string CreatedByDisplay { get; private set; }

    public bool IsWithdrawn { get; private set; }

    public DateTimeOffset? WithdrawnAt { get; private set; }

    /// <summary>
    /// Withdraws the package so it can no longer be deployed. Idempotent.
    /// </summary>
    /// <remarks>
    /// Withdrawal does not touch endpoints that already have the driver installed.
    /// It stops new deployments; removing a driver from a machine is a different
    /// operation with different risks and is not offered here.
    /// </remarks>
    public bool Withdraw(DateTimeOffset now)
    {
        if (IsWithdrawn)
        {
            return false;
        }

        IsWithdrawn = true;
        WithdrawnAt = now;
        return true;
    }

    private static string ValidateSha256(string sha256)
    {
        var normalized = Guard.NotNullOrWhiteSpace(sha256, nameof(sha256), maxLength: 64).Trim().ToLowerInvariant();

        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("SHA-256 must be 64 hexadecimal characters.", nameof(sha256));
        }

        return normalized;
    }

    /// <summary>
    /// Rejects a file name carrying any path at all.
    /// </summary>
    /// <remarks>
    /// The stored name is echoed back to the endpoint, so it must not be able to
    /// express a location. Nothing downstream joins it to a path, but the cheapest
    /// place to make that impossible is where the value is created.
    /// </remarks>
    private static string ValidateFileName(string fileName)
    {
        var value = Guard.NotNullOrWhiteSpace(fileName, nameof(fileName), maxLength: 256).Trim();

        if (value.Contains('/') || value.Contains('\\') || value.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(value) || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("File name must be a bare name with no path.", nameof(fileName));
        }

        return value;
    }

    private static string ValidateInfFileName(string infFileName)
    {
        var value = ValidateFileName(infFileName);

        if (!value.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The driver entry point must be an .inf file.", nameof(infFileName));
        }

        return value;
    }
}
