using System.Buffers.Binary;
using System.Text;

namespace EndpointPlatform.Infrastructure.Agents;

/// <summary>Whether a ProductVersion was read, and if not, what was missing.</summary>
public enum MsiProductVersionOutcome
{
    Found,

    /// <summary>
    /// The compound file has no string pool, so whatever it is, it is not a
    /// Windows Installer database.
    /// </summary>
    NoStringPool,

    /// <summary>The database has no Property table.</summary>
    NoPropertyTable,

    /// <summary>The Property table carries no ProductVersion row, or an empty one.</summary>
    NotDeclared,

    /// <summary>The database streams are present but do not decode.</summary>
    Malformed,
}

/// <summary>The result of reading an MSI's ProductVersion.</summary>
public readonly record struct MsiProductVersion(MsiProductVersionOutcome Outcome, string? Value)
{
    public bool IsFound => Outcome == MsiProductVersionOutcome.Found;

    public static MsiProductVersion Found(string value) => new(MsiProductVersionOutcome.Found, value);

    public static MsiProductVersion Absent(MsiProductVersionOutcome outcome) => new(outcome, null);
}

/// <summary>
/// Reads the <c>ProductVersion</c> property out of a Windows Installer database.
/// </summary>
/// <remarks>
/// <para>
/// Exists because the server publishes agent MSIs from Linux, where there is no
/// <c>msi.dll</c> to ask. What it needs is small and fully specified: the string
/// pool (<c>_StringPool</c> + <c>_StringData</c>) and the <c>Property</c> table,
/// which is two string columns stored column-major. Nothing else in the database
/// is touched, and nothing is written.
/// </para>
/// <para>
/// Stream names inside an MSI are not the table names. Windows Installer packs
/// each pair of characters from a 64-symbol alphabet into one UTF-16 code unit in
/// the U+3800 range, and prefixes every database stream -- the tables and the
/// string pool alike -- with U+4840. <see cref="EncodeStreamName"/> produces
/// exactly what the directory holds, which is why <c>Property</c> is found and
/// a literal search for the word would not be. Measured against a package WiX
/// built, not taken from the specification alone: the string pool carries the
/// marker too, which a generated test database had agreed with this reader in
/// getting wrong.
/// </para>
/// <para>
/// The input is whatever an administrator uploaded, so it is not trusted: every
/// length is checked against the stream it indexes, the entry and row counts are
/// capped, and a structurally hostile file yields <see cref="MsiProductVersionOutcome.Malformed"/>
/// rather than an exception. A file this cannot read is a file that cannot be
/// published -- the caller fails closed on every outcome but
/// <see cref="MsiProductVersionOutcome.Found"/>.
/// </para>
/// </remarks>
public static class MsiDatabase
{
    private const string StringPoolStream = "_StringPool";
    private const string StringDataStream = "_StringData";
    private const string PropertyTable = "Property";
    private const string ProductVersionProperty = "ProductVersion";

    /// <summary>Hard caps. A real MSI is nowhere near either.</summary>
    private const int MaxStrings = 1 << 20;
    private const int MaxRows = 1 << 16;

    /// <summary>Windows Installer's stream-name alphabet, in index order.</summary>
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz._";

    private const char DatabasePrefix = (char)0x4840;
    private const int PairBase = 0x3800;
    private const int SingleBase = 0x4800;

    /// <summary>Set in the string pool header when string references are three bytes wide.</summary>
    private const int LongStringRefsFlag = 0x8000;

    private const int Utf8CodePage = 65001;

    /// <summary>
    /// The name a stream is stored under inside the compound file.
    /// </summary>
    /// <param name="database">
    /// Whether the stream belongs to the installer database: a table, or the
    /// string pool that backs every table. Database streams carry a marker code
    /// unit in front; the summary information and signature streams are stored
    /// under their plain names.
    /// </param>
    public static string EncodeStreamName(string name, bool database)
    {
        ArgumentNullException.ThrowIfNull(name);

        var encoded = new StringBuilder(name.Length / 2 + 2);
        if (database)
        {
            encoded.Append(DatabasePrefix);
        }

        var i = 0;
        while (i < name.Length)
        {
            var first = Alphabet.IndexOf(name[i]);
            if (first < 0)
            {
                // Outside the alphabet: stored as itself.
                encoded.Append(name[i]);
                i++;
                continue;
            }

            if (i + 1 < name.Length)
            {
                var second = Alphabet.IndexOf(name[i + 1]);
                if (second >= 0)
                {
                    encoded.Append((char)(PairBase + first + (second << 6)));
                    i += 2;
                    continue;
                }
            }

            encoded.Append((char)(SingleBase + first));
            i++;
        }

        return encoded.ToString();
    }

    /// <summary>
    /// The database's ProductVersion, or why it could not be read. Never throws
    /// on bad input.
    /// </summary>
    public static MsiProductVersion TryReadProductVersion(ReadOnlyMemory<byte> msi)
    {
        try
        {
            return ReadProductVersion(msi);
        }
        catch (Exception ex) when (ex is ArgumentException
            or IndexOutOfRangeException
            or OverflowException
            or DecoderFallbackException)
        {
            return MsiProductVersion.Absent(MsiProductVersionOutcome.Malformed);
        }
    }

    private static MsiProductVersion ReadProductVersion(ReadOnlyMemory<byte> msi)
    {
        var pool = CompoundFile.TryReadStream(msi, EncodeStreamName(StringPoolStream, database: true));
        var data = CompoundFile.TryReadStream(msi, EncodeStreamName(StringDataStream, database: true));
        if (pool is null || data is null)
        {
            return MsiProductVersion.Absent(MsiProductVersionOutcome.NoStringPool);
        }

        var strings = DecodeStringPool(pool, data, out var longRefs);
        if (strings is null)
        {
            return MsiProductVersion.Absent(MsiProductVersionOutcome.Malformed);
        }

        var property = CompoundFile.TryReadStream(msi, EncodeStreamName(PropertyTable, database: true));
        if (property is null)
        {
            return MsiProductVersion.Absent(MsiProductVersionOutcome.NoPropertyTable);
        }

        // Two string columns, stored column-major: every Property reference, then
        // every Value reference. The row count follows from the stream length.
        var width = longRefs ? 3 : 2;
        var rowSize = width * 2;
        if (property.Length % rowSize != 0)
        {
            return MsiProductVersion.Absent(MsiProductVersionOutcome.Malformed);
        }

        var rows = property.Length / rowSize;
        if (rows > MaxRows)
        {
            return MsiProductVersion.Absent(MsiProductVersionOutcome.Malformed);
        }

        for (var row = 0; row < rows; row++)
        {
            var nameRef = ReadReference(property, row * width, width);
            if (nameRef <= 0 || nameRef >= strings.Count
                || !string.Equals(strings[nameRef], ProductVersionProperty, StringComparison.Ordinal))
            {
                continue;
            }

            var valueRef = ReadReference(property, rows * width + row * width, width);
            if (valueRef <= 0 || valueRef >= strings.Count)
            {
                return MsiProductVersion.Absent(MsiProductVersionOutcome.NotDeclared);
            }

            var value = strings[valueRef].Trim();
            return value.Length == 0
                ? MsiProductVersion.Absent(MsiProductVersionOutcome.NotDeclared)
                : MsiProductVersion.Found(value);
        }

        return MsiProductVersion.Absent(MsiProductVersionOutcome.NotDeclared);
    }

    /// <summary>
    /// The string pool as a 1-based list: index 0 is the null string, so a table's
    /// reference indexes it directly.
    /// </summary>
    /// <remarks>
    /// The pool is a four-byte header (code page, and a flag word whose high bit
    /// selects three-byte references) followed by one four-byte entry per string:
    /// a 16-bit length and a 16-bit reference count. A string longer than 64 KB
    /// is marked by a zero length with a non-zero count, with the real 32-bit
    /// length in the following slot. The bytes themselves are laid out
    /// back-to-back in <c>_StringData</c>, in pool order.
    /// </remarks>
    private static List<string>? DecodeStringPool(byte[] pool, byte[] data, out bool longRefs)
    {
        longRefs = false;
        if (pool.Length < 4 || pool.Length % 4 != 0)
        {
            return null;
        }

        var low = BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(0));
        var high = BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(2));
        longRefs = (high & LongStringRefsFlag) != 0;
        var codePage = low | ((high & ~LongStringRefsFlag) << 16);

        // Strict on purpose: bytes that are not the declared encoding are a reason
        // to refuse, not something to paper over with replacement characters.
        Encoding encoding = codePage == Utf8CodePage
            ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            : Encoding.Latin1;

        var strings = new List<string> { string.Empty };
        var offset = 0;
        var entries = pool.Length / 4;

        for (var i = 1; i < entries; i++)
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(i * 4));
            var references = BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(i * 4 + 2));

            if (length == 0 && references != 0)
            {
                if (i + 1 >= entries)
                {
                    return null;
                }

                var longLength = BinaryPrimitives.ReadUInt32LittleEndian(pool.AsSpan((i + 1) * 4));
                if (longLength > int.MaxValue)
                {
                    return null;
                }

                length = (int)longLength;
                i++;
            }

            if (offset + length > data.Length)
            {
                return null;
            }

            strings.Add(length == 0 ? string.Empty : encoding.GetString(data, offset, length));
            offset += length;

            if (strings.Count > MaxStrings)
            {
                return null;
            }
        }

        return strings;
    }

    private static int ReadReference(byte[] table, int offset, int width)
    {
        if (offset + width > table.Length)
        {
            return -1;
        }

        return width == 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(offset))
            : table[offset] | (table[offset + 1] << 8) | (table[offset + 2] << 16);
    }
}
