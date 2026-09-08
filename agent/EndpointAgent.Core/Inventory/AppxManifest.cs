using System.Xml;

namespace EndpointAgent.Core.Inventory;

/// <summary>
/// What a package's <c>AppxManifest.xml</c> declares about the package.
/// </summary>
/// <remarks>
/// <para>
/// The manifest is the package's own statement of identity, and Windows verified
/// it against the package signature when the package was deployed: the
/// <c>Identity/@Publisher</c> is required to equal the signing certificate's
/// subject, which is what makes a package family name an identity assigned
/// rather than claimed. The manifest is read as XML data with a reader that
/// resolves nothing -- no DTD, no external entities, no network -- and only the
/// handful of elements discovery needs are looked at.
/// </para>
/// <para>
/// A manifest can name its display strings by resource reference
/// (<c>ms-resource:...</c>) rather than literally; those are reported as absent,
/// and the caller decides what to fall back to. Resolving a resource reference
/// means asking Windows to load the package's resource index, which is a
/// platform concern and not something an XML reader should do.
/// </para>
/// </remarks>
/// <param name="Name">The package identity name (<c>com.tinyspeck.slackdesktop</c>).</param>
/// <param name="Publisher">The publisher subject the package is signed as (<c>CN=..., O=...</c>).</param>
/// <param name="Version">The four-part package version.</param>
/// <param name="Architecture">The processor architecture (<c>x64</c>, <c>neutral</c>, ...), when declared.</param>
/// <param name="DisplayName">The literal display name, or null when it is a resource reference or absent.</param>
/// <param name="PublisherDisplayName">The literal publisher display name, or null likewise.</param>
/// <param name="IsFramework">A framework package: a dependency other packages use, never an application.</param>
/// <param name="IsResourcePackage">A resource package: language or scale assets for another package.</param>
/// <param name="Executable">The first declared application's executable, relative to the package root, when declared.</param>
public sealed record AppxManifest(
    string Name,
    string? Publisher,
    string? Version,
    string? Architecture,
    string? DisplayName,
    string? PublisherDisplayName,
    bool IsFramework,
    bool IsResourcePackage,
    string? Executable)
{
    /// <summary>A manifest is tens of kilobytes at most; one larger than this is not worth reading.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    private const string ResourcePrefix = "ms-resource:";

    private static readonly XmlReaderSettings SafeSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = MaxBytes,
        CloseInput = false,
    };

    /// <summary>Reads a manifest, or returns null when the text is not a well-formed package manifest.</summary>
    public static AppxManifest? Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > MaxBytes)
        {
            return null;
        }

        using var text = new StringReader(xml);
        return Parse(() => XmlReader.Create(text, SafeSettings));
    }

    /// <summary>Reads a manifest from a stream, or returns null when it cannot be read as one.</summary>
    public static AppxManifest? Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return Parse(() => XmlReader.Create(stream, SafeSettings));
    }

    /// <summary>
    /// The package family name Windows derives: the identity name plus the
    /// publisher id, which is the last segment of the package full name.
    /// </summary>
    /// <remarks>
    /// Taken from the full name rather than recomputed: the id is Windows' own
    /// hash of the publisher subject, and copying a value Windows already
    /// computed is safer than reimplementing it.
    /// </remarks>
    public static string? FamilyNameOf(string? packageFullName)
    {
        var parts = FullNameParts(packageFullName);
        return parts is null ? null : parts[0] + "_" + parts[4];
    }

    /// <summary>
    /// The five segments of a package full name -- name, version, architecture,
    /// resource id, publisher id -- or null when the value is not one.
    /// </summary>
    public static string[]? FullNameParts(string? packageFullName)
    {
        if (string.IsNullOrWhiteSpace(packageFullName))
        {
            return null;
        }

        // Name_Version_Architecture_ResourceId_PublisherId. The resource id is
        // usually empty, which is why a full name usually has "__" in it.
        var parts = packageFullName.Trim().Split('_');
        return parts.Length == 5 && parts[0].Length > 0 && parts[4].Length > 0 ? parts : null;
    }

    /// <summary>
    /// The common name in a publisher subject (<c>CN=Contoso Ltd, O=...</c>),
    /// or null when there is none. Quoted values, which Windows writes when the
    /// name contains a comma, are unwrapped.
    /// </summary>
    public static string? CommonNameOf(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        var at = subject.IndexOf("CN=", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        var rest = subject[(at + 3)..].TrimStart();
        string value;

        if (rest.StartsWith('"'))
        {
            var close = rest.IndexOf('"', 1);
            value = close < 0 ? rest[1..] : rest[1..close];
        }
        else
        {
            var comma = rest.IndexOf(',');
            value = comma < 0 ? rest : rest[..comma];
        }

        return Value(value);
    }

    private static AppxManifest? Parse(Func<XmlReader> open)
    {
        try
        {
            using var reader = open();
            return Read(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static AppxManifest? Read(XmlReader reader)
    {
        if (!reader.ReadToFollowing("Package") || reader.Depth != 0 || reader.IsEmptyElement)
        {
            return null;
        }

        string? name = null;
        string? publisher = null;
        string? version = null;
        string? architecture = null;
        string? displayName = null;
        string? publisherDisplayName = null;
        var framework = false;
        var resource = false;
        var identityResourceId = false;
        string? executable = null;

        // Walk the direct children of Package; step over everything else.
        if (!reader.Read())
        {
            return null;
        }

        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 0)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1)
            {
                if (!reader.Read())
                {
                    break;
                }

                continue;
            }

            switch (reader.LocalName)
            {
                case "Identity":
                    name = Value(reader.GetAttribute("Name"));
                    publisher = Value(reader.GetAttribute("Publisher"));
                    version = Value(reader.GetAttribute("Version"));
                    architecture = Value(reader.GetAttribute("ProcessorArchitecture"));
                    // A resource package's identity carries a split resource id
                    // ("split.language-de", "split.scale-200"). Windows also writes
                    // ResourceId="neutral" on ordinary packages, which is not one.
                    identityResourceId = Value(reader.GetAttribute("ResourceId")) is { } resourceId
                        && resourceId.StartsWith("split.", StringComparison.OrdinalIgnoreCase);
                    reader.Skip();
                    break;

                case "Properties":
                    ReadProperties(reader, ref displayName, ref publisherDisplayName, ref framework, ref resource);
                    break;

                case "Applications":
                    executable = ReadFirstExecutable(reader);
                    break;

                default:
                    reader.Skip();
                    break;
            }
        }

        if (name is null)
        {
            return null;
        }

        return new AppxManifest(
            name, publisher, version, architecture, displayName, publisherDisplayName,
            framework, resource || identityResourceId, executable);
    }

    /// <summary>Reads the simple children of Properties; leaves the reader after the element.</summary>
    private static void ReadProperties(
        XmlReader reader, ref string? displayName, ref string? publisherDisplayName, ref bool framework, ref bool resource)
    {
        if (reader.IsEmptyElement)
        {
            reader.Skip();
            return;
        }

        var depth = reader.Depth;
        reader.Read();

        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                reader.Read();
                return;
            }

            if (reader.NodeType != XmlNodeType.Element || reader.Depth != depth + 1)
            {
                reader.Read();
                continue;
            }

            // ReadElementContentAsString consumes the element and lands on the node
            // after it, so the loop must not read again on these branches.
            switch (reader.LocalName)
            {
                case "DisplayName":
                    displayName = Literal(reader.ReadElementContentAsString());
                    break;
                case "PublisherDisplayName":
                    publisherDisplayName = Literal(reader.ReadElementContentAsString());
                    break;
                case "Framework":
                    framework = IsTrue(reader.ReadElementContentAsString());
                    break;
                case "ResourcePackage":
                    resource = IsTrue(reader.ReadElementContentAsString());
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
    }

    /// <summary>The first Application's executable; leaves the reader after the Applications element.</summary>
    private static string? ReadFirstExecutable(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            reader.Skip();
            return null;
        }

        var depth = reader.Depth;
        string? executable = null;
        reader.Read();

        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                reader.Read();
                return executable;
            }

            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1)
            {
                if (executable is null && reader.LocalName == "Application")
                {
                    executable = Value(reader.GetAttribute("Executable"));
                }

                reader.Skip();
                continue;
            }

            reader.Read();
        }

        return executable;
    }

    /// <summary>A literal string; a resource reference is reported as absent.</summary>
    private static string? Literal(string? raw)
    {
        var value = Value(raw);
        return value is null || value.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase) ? null : value;
    }

    private static bool IsTrue(string? raw) => string.Equals(Value(raw), "true", StringComparison.OrdinalIgnoreCase);

    private static string? Value(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
}
