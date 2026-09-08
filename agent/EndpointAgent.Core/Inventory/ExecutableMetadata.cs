namespace EndpointAgent.Core.Inventory;

/// <summary>Whether an executable carries an embedded Authenticode signature.</summary>
/// <remarks>
/// <para>
/// <b>This is presence, not trust.</b> <see cref="Signed"/> means the file carries
/// a signature block whose signing certificate could be read; it says nothing
/// about whether that certificate chains to a trusted root or whether the file
/// still matches the signature. The subject it names is therefore what the file
/// <em>claims</em>, useful for grouping and for an administrator's eye, and not
/// an identity the platform relies on for any decision. Trust verification
/// (<c>WinVerifyTrust</c>) stays where a decision depends on it -- the agent
/// update path -- and would be a separate, costed step here.
/// </para>
/// <para>
/// Windows' own binaries are mostly catalog-signed rather than embedded-signed,
/// so they read as <see cref="Unsigned"/> here. That is accurate for what was
/// asked, and the reason the value is named for what was checked.
/// </para>
/// </remarks>
public enum ExecutableSignatureStatus
{
    /// <summary>The file carries no embedded signature.</summary>
    Unsigned,

    /// <summary>The file carries an embedded signature and its signing certificate was read.</summary>
    Signed,

    /// <summary>The file could not be examined (locked, inaccessible, or not a valid image).</summary>
    Unreadable,
}

/// <summary>
/// What an executable says about itself: its version resource and its
/// embedded signature.
/// </summary>
/// <remarks>
/// Every field is what the file declares. A version resource is free text an
/// author wrote; the signer subject is a claim the file carries. Absent values
/// are null, never guessed.
/// </remarks>
public sealed record ExecutableMetadata(
    string? ProductName,
    string? FileVersion,
    string? CompanyName,
    string? OriginalFilename,
    string? FileDescription,
    string? SignerSubject,
    ExecutableSignatureStatus SignatureStatus);
