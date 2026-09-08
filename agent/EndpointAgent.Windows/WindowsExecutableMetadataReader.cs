using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Inventory;

namespace EndpointAgent.Windows;

/// <summary>
/// Reads an executable's version resource and embedded signature.
/// </summary>
/// <remarks>
/// <para>
/// Two reads, both of the file as data. The version resource comes through
/// <see cref="FileVersionInfo"/>, which parses the resource section. The signing
/// certificate comes through <see cref="X509Certificate.CreateFromSignedFile"/>,
/// which parses the signature block and returns the leaf certificate without
/// evaluating its chain -- which is why the result is a <em>claim</em>; see
/// <see cref="ExecutableSignatureStatus"/>. Neither loads the image.
/// </para>
/// <para>
/// Unsigned is distinguished from unreadable by the error the signature read
/// reports: "no signature" is an answer about the file, everything else is a
/// failure to look.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsExecutableMetadataReader : IExecutableMetadataReader
{
    /// <summary>TRUST_E_NOSIGNATURE: the subject is not signed.</summary>
    private const int TrustENoSignature = unchecked((int)0x800B0100);

    /// <summary>CRYPT_E_NO_MATCH: no signature object found in the file, which is how an unsigned PE reads.</summary>
    private const int CryptENoMatch = unchecked((int)0x80092009);

    /// <inheritdoc />
    public ExecutableMetadata? Read(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        FileVersionInfo version;
        try
        {
            if (!File.Exists(executablePath))
            {
                return null;
            }

            version = FileVersionInfo.GetVersionInfo(executablePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return null;
        }

        var (subject, status) = ReadSignature(executablePath);

        return new ExecutableMetadata(
            Value(version.ProductName),
            Value(version.FileVersion),
            Value(version.CompanyName),
            Value(version.OriginalFilename),
            Value(version.FileDescription),
            subject,
            status);
    }

    private static (string? Subject, ExecutableSignatureStatus Status) ReadSignature(string path)
    {
        try
        {
            using var certificate = X509Certificate.CreateFromSignedFile(path);
            return (Value(certificate.Subject), ExecutableSignatureStatus.Signed);
        }
        catch (CryptographicException ex) when (ex.HResult is TrustENoSignature or CryptENoMatch)
        {
            return (null, ExecutableSignatureStatus.Unsigned);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or SecurityException)
        {
            return (null, ExecutableSignatureStatus.Unreadable);
        }
    }

    private static string? Value(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
}
