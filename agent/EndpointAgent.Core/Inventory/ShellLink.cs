using System.Buffers.Binary;
using System.Text;

namespace EndpointAgent.Core.Inventory;

/// <summary>
/// What a Windows shortcut (<c>.lnk</c>) points at, read from its bytes.
/// </summary>
/// <remarks>
/// <para>
/// A direct reader of the Shell Link binary format (MS-SHLLINK), and deliberately
/// not the shell's own <c>IShellLink</c>. That is a COM object that
/// <em>resolves</em> a link, and resolving can search for a moved target, show a
/// prompt, or reach for the network. This reads what the file says and nothing
/// more: nothing here runs, resolves or follows anything, and the target is data.
/// </para>
/// <para>
/// Only the parts discovery needs are read -- the LinkInfo local path, the string
/// data, and two extra-data blocks: the environment-variable target that
/// installers write for <c>%ProgramFiles%</c>-relative shortcuts, and the Darwin
/// marker that says a shortcut is a Windows Installer advertisement. The target
/// ID list is skipped over, never interpreted.
/// </para>
/// <para>
/// A shortcut is whatever sits in a Start Menu folder, which a user or an
/// installer wrote, so every length is checked against the bytes it indexes and
/// a malformed file yields null rather than an exception.
/// </para>
/// </remarks>
/// <param name="LocalPath">The target as LinkInfo records it, when the target is a local file.</param>
/// <param name="EnvironmentPath">The target with environment variables unexpanded, when the shortcut carries one.</param>
/// <param name="IsAdvertised">Whether the shortcut is a Windows Installer advertisement rather than a plain file link.</param>
public sealed record ShellLink(
    string? LocalPath,
    string? EnvironmentPath,
    string? RelativePath,
    string? WorkingDirectory,
    string? Arguments,
    string? Description,
    bool IsAdvertised)
{
    private const int HeaderSize = 0x4C;

    /// <summary>CLSID_ShellLink, {00021401-0000-0000-C000-000000000046}, as stored.</summary>
    private static ReadOnlySpan<byte> LinkClsid =>
        [0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46];

    // LinkFlags (MS-SHLLINK 2.1.1)
    private const uint HasLinkTargetIdList = 0x01;
    private const uint HasLinkInfo = 0x02;
    private const uint HasName = 0x04;
    private const uint HasRelativePath = 0x08;
    private const uint HasWorkingDir = 0x10;
    private const uint HasArguments = 0x20;
    private const uint HasIconLocation = 0x40;
    private const uint IsUnicode = 0x80;

    // LinkInfoFlags (MS-SHLLINK 2.3)
    private const uint VolumeIdAndLocalBasePath = 0x01;
    private const int LinkInfoMinimumHeader = 0x1C;
    private const int LinkInfoHeaderWithUnicode = 0x24;

    // ExtraData (MS-SHLLINK 2.5)
    private const uint EnvironmentVariableDataBlock = 0xA0000001;
    private const uint DarwinDataBlock = 0xA0000006;
    private const int EnvironmentVariableDataBlockSize = 0x314;
    private const int EnvironmentTargetAnsiOffset = 8;
    private const int EnvironmentTargetAnsiLength = 260;
    private const int EnvironmentTargetUnicodeOffset = EnvironmentTargetAnsiOffset + EnvironmentTargetAnsiLength;
    private const int EnvironmentTargetUnicodeLength = 520;

    private const int MaxExtraDataBlocks = 64;
    private const int MaxStringCharacters = 4096;

    /// <summary>
    /// Reads a shortcut, or returns null when the bytes are not a well-formed one.
    /// </summary>
    public static ShellLink? Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return Read(bytes);
        }
        catch (Exception ex) when (ex is FormatException
            or ArgumentOutOfRangeException
            or IndexOutOfRangeException
            or OverflowException
            or DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Expands <c>%NAME%</c> references in an environment-relative target, or
    /// returns null when any name is unknown.
    /// </summary>
    /// <remarks>
    /// A shortcut's variables are expanded against a fixed, caller-supplied set
    /// rather than the agent's own environment: the agent runs as LocalSystem, so
    /// its <c>%USERPROFILE%</c> is not the user's, and an installer-written
    /// <c>%ProgramFiles%</c> must mean the machine's. An unknown name refuses the
    /// whole path, because a half-expanded path is not a path.
    /// </remarks>
    public static string? ExpandEnvironment(string? path, Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var expanded = new StringBuilder(path.Length);
        var at = 0;

        while (at < path.Length)
        {
            var open = path.IndexOf('%', at);
            if (open < 0)
            {
                expanded.Append(path, at, path.Length - at);
                break;
            }

            var close = path.IndexOf('%', open + 1);
            if (close < 0)
            {
                // An unmatched sign: not an expansion, and not something to guess at.
                return null;
            }

            expanded.Append(path, at, open - at);

            var name = path[(open + 1)..close];
            if (name.Length == 0)
            {
                // "%%" is a literal percent sign.
                expanded.Append('%');
            }
            else
            {
                var value = lookup(name);
                if (string.IsNullOrEmpty(value))
                {
                    return null;
                }

                expanded.Append(value);
            }

            at = close + 1;
        }

        return expanded.ToString();
    }

    private static ShellLink Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != HeaderSize
            || !bytes.Slice(4, 16).SequenceEqual(LinkClsid))
        {
            throw Malformed();
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]);
        var unicode = (flags & IsUnicode) != 0;
        var position = HeaderSize;

        if ((flags & HasLinkTargetIdList) != 0)
        {
            var size = BinaryPrimitives.ReadUInt16LittleEndian(bytes[position..]);
            position = checked(position + 2 + size);
            if (position > bytes.Length)
            {
                throw Malformed();
            }
        }

        string? localPath = null;
        if ((flags & HasLinkInfo) != 0)
        {
            var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[position..]));
            if (size < LinkInfoMinimumHeader || checked(position + size) > bytes.Length)
            {
                throw Malformed();
            }

            localPath = ReadLocalPath(bytes.Slice(position, size));
            position += size;
        }

        var description = (flags & HasName) != 0 ? ReadString(bytes, ref position, unicode) : null;
        var relativePath = (flags & HasRelativePath) != 0 ? ReadString(bytes, ref position, unicode) : null;
        var workingDirectory = (flags & HasWorkingDir) != 0 ? ReadString(bytes, ref position, unicode) : null;
        var arguments = (flags & HasArguments) != 0 ? ReadString(bytes, ref position, unicode) : null;
        if ((flags & HasIconLocation) != 0)
        {
            ReadString(bytes, ref position, unicode);
        }

        string? environmentPath = null;
        var advertised = false;

        for (var blocks = 0; blocks < MaxExtraDataBlocks && position + 4 <= bytes.Length; blocks++)
        {
            var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[position..]));
            if (size < 4)
            {
                break; // The terminal block.
            }

            if (size < 8 || checked(position + size) > bytes.Length)
            {
                throw Malformed();
            }

            var block = bytes.Slice(position, size);
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);

            if (signature == EnvironmentVariableDataBlock && size == EnvironmentVariableDataBlockSize)
            {
                environmentPath = ReadEnvironmentTarget(block);
            }
            else if (signature == DarwinDataBlock)
            {
                advertised = true;
            }

            position += size;
        }

        return new ShellLink(localPath, environmentPath, relativePath, workingDirectory, arguments, description, advertised);
    }

    /// <summary>LocalBasePath + CommonPathSuffix, preferring the Unicode forms when the file carries them.</summary>
    private static string? ReadLocalPath(ReadOnlySpan<byte> linkInfo)
    {
        var headerSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(linkInfo[4..]));
        var linkInfoFlags = BinaryPrimitives.ReadUInt32LittleEndian(linkInfo[8..]);

        if ((linkInfoFlags & VolumeIdAndLocalBasePath) == 0)
        {
            // A network-relative link, or no path at all. Neither is a local file.
            return null;
        }

        if (headerSize < LinkInfoMinimumHeader || headerSize > linkInfo.Length)
        {
            throw Malformed();
        }

        var basePathOffset = BinaryPrimitives.ReadUInt32LittleEndian(linkInfo[16..]);
        var suffixOffset = BinaryPrimitives.ReadUInt32LittleEndian(linkInfo[24..]);

        string basePath;
        string suffix;

        if (headerSize >= LinkInfoHeaderWithUnicode)
        {
            var basePathUnicode = BinaryPrimitives.ReadUInt32LittleEndian(linkInfo[28..]);
            var suffixUnicode = BinaryPrimitives.ReadUInt32LittleEndian(linkInfo[32..]);

            basePath = basePathUnicode != 0 ? ReadUtf16Z(linkInfo, basePathUnicode) : ReadAnsiZ(linkInfo, basePathOffset);
            suffix = suffixUnicode != 0 ? ReadUtf16Z(linkInfo, suffixUnicode) : ReadAnsiZ(linkInfo, suffixOffset);
        }
        else
        {
            basePath = ReadAnsiZ(linkInfo, basePathOffset);
            suffix = ReadAnsiZ(linkInfo, suffixOffset);
        }

        var path = basePath + suffix;
        return path.Length == 0 ? null : path;
    }

    private static string? ReadEnvironmentTarget(ReadOnlySpan<byte> block)
    {
        var unicode = ReadUtf16Bounded(block.Slice(EnvironmentTargetUnicodeOffset, EnvironmentTargetUnicodeLength));
        if (unicode.Length > 0)
        {
            return unicode;
        }

        var ansi = ReadAnsiBounded(block.Slice(EnvironmentTargetAnsiOffset, EnvironmentTargetAnsiLength));
        return ansi.Length == 0 ? null : ansi;
    }

    /// <summary>One StringData entry: a character count, then the characters.</summary>
    private static string ReadString(ReadOnlySpan<byte> bytes, ref int position, bool unicode)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[position..]);
        if (count > MaxStringCharacters)
        {
            throw Malformed();
        }

        position += 2;
        var length = unicode ? count * 2 : count;
        if (checked(position + length) > bytes.Length)
        {
            throw Malformed();
        }

        var value = unicode
            ? Encoding.Unicode.GetString(bytes.Slice(position, length))
            : Encoding.Latin1.GetString(bytes.Slice(position, length));

        position += length;
        return value;
    }

    private static string ReadAnsiZ(ReadOnlySpan<byte> span, uint offset)
    {
        var start = checked((int)offset);
        if (start > span.Length)
        {
            throw Malformed();
        }

        return ReadAnsiBounded(span[start..]);
    }

    private static string ReadUtf16Z(ReadOnlySpan<byte> span, uint offset)
    {
        var start = checked((int)offset);
        if (start > span.Length)
        {
            throw Malformed();
        }

        return ReadUtf16Bounded(span[start..]);
    }

    /// <summary>
    /// A null-terminated single-byte string. Decoded as Latin-1 rather than the
    /// machine's ANSI code page, which is not available to platform-neutral
    /// code: for ASCII paths -- the overwhelming case -- the two agree, and
    /// Windows writes the Unicode form alongside whenever they would not.
    /// </summary>
    private static string ReadAnsiBounded(ReadOnlySpan<byte> span)
    {
        var end = span.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? span : span[..end]);
    }

    private static string ReadUtf16Bounded(ReadOnlySpan<byte> span)
    {
        var end = -1;
        for (var i = 0; i + 1 < span.Length; i += 2)
        {
            if (span[i] == 0 && span[i + 1] == 0)
            {
                end = i;
                break;
            }
        }

        var text = end < 0 ? span[..(span.Length & ~1)] : span[..end];
        return Encoding.Unicode.GetString(text);
    }

    private static FormatException Malformed() => new("Not a well-formed shell link.");
}
