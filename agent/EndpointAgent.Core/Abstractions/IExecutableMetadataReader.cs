using EndpointAgent.Core.Inventory;

namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Reads what an executable file says about itself.
/// </summary>
/// <remarks>
/// The one place discovery opens an executable, and it opens it to read: the
/// version resource and the signature block. The file is never loaded, mapped
/// as code, or run. The Windows implementation is in the platform assembly; the
/// composite collector asks it once per distinct executable a supplementary
/// source referenced.
/// </remarks>
public interface IExecutableMetadataReader
{
    /// <summary>
    /// The file's declared metadata, or null when there is no such file or it
    /// cannot be opened at all.
    /// </summary>
    ExecutableMetadata? Read(string executablePath);
}
