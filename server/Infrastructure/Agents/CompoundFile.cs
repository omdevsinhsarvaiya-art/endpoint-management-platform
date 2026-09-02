using System.Buffers.Binary;
using System.Text;

namespace EndpointPlatform.Infrastructure.Agents;

/// <summary>
/// Reads one named stream out of an OLE2 Compound File (the MSI container).
/// </summary>
/// <remarks>
/// <para>
/// Written for exactly one purpose: locating the <c>\005DigitalSignature</c>
/// stream, which is where Windows Installer keeps an Authenticode PKCS#7 blob.
/// Only what that needs is implemented -- header, FAT, DIFAT, directory, and the
/// mini-stream -- and nothing is written. A general OLE library would be several
/// thousand lines and a new dependency for the sake of one lookup.
/// </para>
/// <para>
/// The input is untrusted: it is whatever an administrator uploaded. Every offset
/// and chain is bounds-checked against the file length and every chain walk is
/// capped, so a crafted file can produce a refusal but not an unbounded loop, an
/// out-of-range read, or an allocation the length field lies about.
/// </para>
/// </remarks>
internal static class CompoundFile
{
    private static ReadOnlySpan<byte> Magic => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSector = 0xFFFFFFFF;
    private const uint DifatSector = 0xFFFFFFFC;
    private const uint FatSector = 0xFFFFFFFD;

    private const int HeaderSize = 512;
    private const int DirectoryEntrySize = 128;
    private const int DifatInHeader = 109;

    private const byte StreamObject = 2;
    private const byte RootStorageObject = 5;

    /// <summary>Hard cap on chain walks; a real MSI is nowhere near it.</summary>
    private const int MaxSectors = 1 << 20;

    /// <summary>True when the bytes begin with the OLE2 signature.</summary>
    public static bool IsCompoundFile(ReadOnlySpan<byte> file) =>
        file.Length >= Magic.Length && file[..Magic.Length].SequenceEqual(Magic);

    /// <summary>
    /// Returns the contents of the named stream, or null when the file is not a
    /// well-formed compound file or holds no such stream. Never throws on bad input.
    /// </summary>
    public static byte[]? TryReadStream(ReadOnlyMemory<byte> file, string streamName)
    {
        try
        {
            return ReadStream(file.Span, streamName);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            // A structurally hostile file. Treated as "no such stream", which the
            // caller reports as unsigned -- the safe direction.
            return null;
        }
    }

    private static byte[]? ReadStream(ReadOnlySpan<byte> file, string streamName)
    {
        if (!IsCompoundFile(file) || file.Length < HeaderSize)
        {
            return null;
        }

        var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(file[0x1E..]);
        var miniShift = BinaryPrimitives.ReadUInt16LittleEndian(file[0x20..]);
        if (sectorShift is < 7 or > 12 || miniShift is < 4 or > 12)
        {
            return null;
        }

        var sectorSize = 1 << sectorShift;
        var miniSize = 1 << miniShift;
        var fatSectorCount = BinaryPrimitives.ReadUInt32LittleEndian(file[0x2C..]);
        var dirStart = BinaryPrimitives.ReadUInt32LittleEndian(file[0x30..]);
        var miniCutoff = BinaryPrimitives.ReadUInt32LittleEndian(file[0x38..]);
        var miniFatStart = BinaryPrimitives.ReadUInt32LittleEndian(file[0x3C..]);
        var miniFatCount = BinaryPrimitives.ReadUInt32LittleEndian(file[0x40..]);
        var difatStart = BinaryPrimitives.ReadUInt32LittleEndian(file[0x44..]);
        var difatCount = BinaryPrimitives.ReadUInt32LittleEndian(file[0x48..]);

        var fat = ReadFat(file, sectorSize, fatSectorCount, difatStart, difatCount);
        if (fat is null)
        {
            return null;
        }

        // ---- directory: find the root (for the mini stream) and the target ----
        uint? rootStart = null;
        ulong rootSize = 0;
        uint? targetStart = null;
        ulong targetSize = 0;
        var wanted = streamName.AsSpan();

        foreach (var sector in Chain(fat, dirStart))
        {
            var offset = SectorOffset(sector, sectorSize);
            if (offset + sectorSize > file.Length)
            {
                return null;
            }

            for (var i = 0; i + DirectoryEntrySize <= sectorSize; i += DirectoryEntrySize)
            {
                var entry = file.Slice(offset + i, DirectoryEntrySize);
                var type = entry[0x42];
                if (type is not (StreamObject or RootStorageObject))
                {
                    continue;
                }

                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(entry[0x40..]);
                if (nameLength is < 2 or > 64)
                {
                    continue;
                }

                // Length includes the terminating null; UTF-16LE.
                var name = Encoding.Unicode.GetString(entry[..(nameLength - 2)]);
                var start = BinaryPrimitives.ReadUInt32LittleEndian(entry[0x74..]);
                var size = BinaryPrimitives.ReadUInt64LittleEndian(entry[0x78..]);
                if (sectorShift == 9)
                {
                    size &= 0xFFFFFFFF; // v3 files only guarantee the low 32 bits
                }

                if (type == RootStorageObject)
                {
                    rootStart = start;
                    rootSize = size;
                }
                else if (name.AsSpan().SequenceEqual(wanted))
                {
                    targetStart = start;
                    targetSize = size;
                }
            }
        }

        if (targetStart is null)
        {
            return null;
        }

        if (targetSize > int.MaxValue || targetSize > (ulong)file.Length)
        {
            return null;
        }

        var length = (int)targetSize;
        if (length == 0)
        {
            return [];
        }

        // ---- small streams live inside the root's mini stream ---------------
        if (targetSize < miniCutoff)
        {
            if (rootStart is null)
            {
                return null;
            }

            var miniStream = ReadChain(file, fat, rootStart.Value, sectorSize, (int)Math.Min(rootSize, (ulong)file.Length));
            if (miniStream is null)
            {
                return null;
            }

            var miniFat = ReadTable(file, fat, miniFatStart, sectorSize, miniFatCount);
            if (miniFat is null)
            {
                return null;
            }

            return ReadMiniChain(miniStream, miniFat, targetStart.Value, miniSize, length);
        }

        return ReadChain(file, fat, targetStart.Value, sectorSize, length);
    }

    private static uint[]? ReadFat(
        ReadOnlySpan<byte> file, int sectorSize, uint fatSectorCount, uint difatStart, uint difatCount)
    {
        if (fatSectorCount == 0 || fatSectorCount > MaxSectors)
        {
            return null;
        }

        // Which sectors hold the FAT: 109 entries in the header, the rest via DIFAT.
        var fatSectors = new List<uint>((int)Math.Min(fatSectorCount, 4096));
        for (var i = 0; i < DifatInHeader && fatSectors.Count < fatSectorCount; i++)
        {
            var s = BinaryPrimitives.ReadUInt32LittleEndian(file[(0x4C + i * 4)..]);
            if (s >= EndOfChain - 1)
            {
                break;
            }

            fatSectors.Add(s);
        }

        var difat = difatStart;
        for (var d = 0; d < difatCount && fatSectors.Count < fatSectorCount; d++)
        {
            if (difat >= EndOfChain - 1)
            {
                break;
            }

            var offset = SectorOffset(difat, sectorSize);
            if (offset + sectorSize > file.Length)
            {
                return null;
            }

            var entries = sectorSize / 4 - 1;
            for (var i = 0; i < entries && fatSectors.Count < fatSectorCount; i++)
            {
                var s = BinaryPrimitives.ReadUInt32LittleEndian(file[(offset + i * 4)..]);
                if (s >= EndOfChain - 1)
                {
                    break;
                }

                fatSectors.Add(s);
            }

            difat = BinaryPrimitives.ReadUInt32LittleEndian(file[(offset + entries * 4)..]);
        }

        var perSector = sectorSize / 4;
        var fat = new uint[checked(fatSectors.Count * perSector)];
        for (var i = 0; i < fatSectors.Count; i++)
        {
            var offset = SectorOffset(fatSectors[i], sectorSize);
            if (offset + sectorSize > file.Length)
            {
                return null;
            }

            for (var j = 0; j < perSector; j++)
            {
                fat[i * perSector + j] = BinaryPrimitives.ReadUInt32LittleEndian(file[(offset + j * 4)..]);
            }
        }

        return fat;
    }

    /// <summary>A sector table (used for the mini-FAT) read through the regular FAT.</summary>
    private static uint[]? ReadTable(ReadOnlySpan<byte> file, uint[] fat, uint start, int sectorSize, uint sectorCount)
    {
        if (sectorCount == 0 || start >= EndOfChain - 1)
        {
            return [];
        }

        var bytes = ReadChain(file, fat, start, sectorSize, checked((int)sectorCount * sectorSize));
        if (bytes is null)
        {
            return null;
        }

        var table = new uint[bytes.Length / 4];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4));
        }

        return table;
    }

    private static byte[]? ReadChain(ReadOnlySpan<byte> file, uint[] fat, uint start, int sectorSize, int length)
    {
        var result = new byte[length];
        var copied = 0;

        foreach (var sector in Chain(fat, start))
        {
            if (copied >= length)
            {
                break;
            }

            var offset = SectorOffset(sector, sectorSize);
            if (offset + sectorSize > file.Length)
            {
                return null;
            }

            var take = Math.Min(sectorSize, length - copied);
            file.Slice(offset, take).CopyTo(result.AsSpan(copied));
            copied += take;
        }

        return copied == length ? result : null;
    }

    private static byte[]? ReadMiniChain(byte[] miniStream, uint[] miniFat, uint start, int miniSize, int length)
    {
        var result = new byte[length];
        var copied = 0;

        foreach (var sector in Chain(miniFat, start))
        {
            if (copied >= length)
            {
                break;
            }

            var offset = checked((long)sector * miniSize);
            if (offset + miniSize > miniStream.Length)
            {
                return null;
            }

            var take = Math.Min(miniSize, length - copied);
            miniStream.AsSpan((int)offset, take).CopyTo(result.AsSpan(copied));
            copied += take;
        }

        return copied == length ? result : null;
    }

    /// <summary>Walks a FAT chain, stopping at end-of-chain, a bad entry, or the cap.</summary>
    private static IEnumerable<uint> Chain(uint[] fat, uint start)
    {
        var current = start;
        for (var guard = 0; guard < MaxSectors; guard++)
        {
            if (current >= EndOfChain - 1 || current == FatSector || current == DifatSector || current == FreeSector)
            {
                yield break;
            }

            yield return current;

            if (current >= fat.Length)
            {
                yield break;
            }

            current = fat[current];
        }
    }

    private static int SectorOffset(uint sector, int sectorSize) =>
        checked((int)((long)(sector + 1) * sectorSize));
}
