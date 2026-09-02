using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Software;

/// <summary>Where package installer bytes are stored, addressed by content hash.</summary>
public sealed class PackageStorageOptions
{
    public const string SectionName = "PackageStorage";

    /// <summary>
    /// Directory holding package content. In production this is a persistent,
    /// access-controlled volume; the runtime account needs read/write here and
    /// nothing else needs it at all.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Directory { get; init; } = "package-content";
}

/// <summary>
/// Content-addressed store for package installer bytes. The store never trusts a
/// caller-supplied hash: it computes the SHA-256 as it writes and rejects a
/// mismatch, so the row in the database and the bytes on disk cannot disagree.
/// </summary>
public interface IPackageContentStore
{
    /// <summary>
    /// Streams <paramref name="content"/> to storage, computing its SHA-256. If the
    /// computed hash does not equal <paramref name="expectedSha256"/> the write is
    /// discarded and an <see cref="InvalidOperationException"/> is thrown.
    /// </summary>
    Task<long> SaveAsync(string expectedSha256, Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams <paramref name="content"/> to storage and returns the SHA-256 the
    /// store computed over the bytes it actually wrote, with the size.
    /// </summary>
    /// <remarks>
    /// The authoritative form of <see cref="SaveAsync"/>. Nothing about the hash is
    /// taken from the caller; the value returned is the only one that should ever be
    /// recorded against the artifact.
    /// </remarks>
    Task<(string Sha256, long SizeBytes)> SaveComputingHashAsync(Stream content, CancellationToken cancellationToken = default);

    /// <summary>Opens the stored content for reading, or null if absent.</summary>
    Task<Stream?> OpenReadAsync(string sha256, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string sha256, CancellationToken cancellationToken = default);
}

/// <summary>Filesystem implementation: one file per content hash.</summary>
public sealed class FileSystemPackageContentStore : IPackageContentStore
{
    private readonly string _directory;

    public FileSystemPackageContentStore(IOptions<PackageStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _directory = options.Value.Directory;
        System.IO.Directory.CreateDirectory(_directory);
    }

    public async Task<(string Sha256, long SizeBytes)> SaveComputingHashAsync(
        Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Hash while writing to a temp file, then place it at its content address.
        // The caller learns the hash only after the bytes are durable under it.
        var tempPath = Path.Combine(_directory, "incoming-" + Guid.CreateVersion7().ToString("N") + ".tmp");
        string computed;
        long written;

        try
        {
            await using (var destination = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                sha.TransformFinalBlock([], 0, 0);
                written = destination.Length;
                computed = Convert.ToHexStringLower(sha.Hash!);
            }

            var finalPath = PathFor(computed);
            if (File.Exists(finalPath))
            {
                File.Delete(tempPath);
            }
            else
            {
                File.Move(tempPath, finalPath);
            }

            return (computed, written);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    public async Task<long> SaveAsync(
        string expectedSha256, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        ArgumentNullException.ThrowIfNull(content);

        var normalizedExpected = expectedSha256.ToLowerInvariant();
        var finalPath = PathFor(normalizedExpected);

        // Stage under a temp name, hash while writing, then atomically publish only
        // if the hash matches. A partial or wrong write never becomes visible.
        var tempPath = finalPath + "." + Guid.CreateVersion7().ToString("N") + ".tmp";
        long written;
        string computed;

        try
        {
            await using (var destination = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                sha.TransformFinalBlock([], 0, 0);
                written = destination.Length;
                computed = Convert.ToHexStringLower(sha.Hash!);
            }

            if (!string.Equals(computed, normalizedExpected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Package content hash does not match the declared SHA-256; upload rejected.");
            }

            // Publish. If another upload of the same content raced us, keep the existing.
            if (File.Exists(finalPath))
            {
                File.Delete(tempPath);
            }
            else
            {
                File.Move(tempPath, finalPath);
            }

            return written;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // Best-effort cleanup of the staging file.
                }
            }
        }
    }

    public Task<Stream?> OpenReadAsync(string sha256, CancellationToken cancellationToken = default)
    {
        var path = PathFor(sha256.ToLowerInvariant());
        Stream? stream = File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true)
            : null;
        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(string sha256, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(PathFor(sha256.ToLowerInvariant())));

    private string PathFor(string sha256)
    {
        // Guard against a hostile hash escaping the directory.
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Invalid content hash.", nameof(sha256));
        }

        return Path.Combine(_directory, sha256 + ".bin");
    }
}
