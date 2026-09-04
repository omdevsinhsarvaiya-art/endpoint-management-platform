using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EndpointPlatform.Infrastructure.Agents;

namespace EndpointPlatform.Infrastructure.Tests.Agents;

/// <summary>
/// Builds MSI-shaped test artifacts: OLE2 compound files that may carry an
/// Authenticode signature stream signed by a throwaway in-memory authority.
/// </summary>
/// <remarks>
/// <para>
/// No real signing certificate exists in this repository or its CI, and none is
/// ever committed. Every key here is generated in memory for one test run and
/// discarded. The verifier under test trusts this authority only when a test
/// explicitly installs it through <see cref="TrustingChainPolicy"/>; production
/// registers <see cref="SystemTrustChainPolicy"/>, so nothing produced here could
/// ever pass a production publish gate. That asymmetry is the point.
/// </para>
/// <para>
/// The compound-file writer is the minimum that produces the files the reader
/// must handle: 512-byte (v3) or 4 KB (v4) sectors, as many FAT and directory
/// sectors as the content needs, and streams in either regular sectors or the
/// mini stream depending on the size cutoff requested. The 4 KB layout matters
/// because it is what Windows Installer writes: the built agent MSI is v4, and
/// its first reading exposed two things the 512-byte artifacts had not.
/// </para>
/// </remarks>
public static class TestArtifacts
{
    private static readonly Oid SpcIndirectData = new("1.3.6.1.4.1.311.2.1.4");
    private const string CodeSigningEku = "1.3.6.1.5.5.7.3.3";
    private const string ServerAuthEku = "1.3.6.1.5.5.7.3.1";

    public const string DefaultLeafSubject = "CN=Techsara Test Signing, O=Techsara Solutions";

    // ---- certificates -------------------------------------------------------

    /// <summary>
    /// A throwaway CA (with its private key, so it can issue more leaves) and a
    /// code-signing leaf issued by it.
    /// </summary>
    public sealed record Authority(X509Certificate2 Root, X509Certificate2 Leaf) : IDisposable
    {
        /// <summary>The root as a trust anchor: public part only.</summary>
        public X509Certificate2 RootPublic => X509CertificateLoader.LoadCertificate(Root.RawData);

        public void Dispose()
        {
            Root.Dispose();
            Leaf.Dispose();
        }
    }

    public static Authority CreateAuthority(
        string leafSubject = DefaultLeafSubject,
        bool leafHasCodeSigningEku = true,
        string rootSubject = "CN=Techsara Test Root CA")
    {
        var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest(rootSubject, rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var root = rootRequest.CreateSelfSigned(notBefore, notBefore.AddYears(5));

        var leaf = IssueLeaf(root, leafSubject, leafHasCodeSigningEku);
        return new Authority(root, leaf);
    }

    /// <summary>Issues another leaf under an authority's root, with the given subject.</summary>
    public static X509Certificate2 IssueLeaf(Authority authority, string subject, bool codeSigningEku = true) =>
        IssueLeaf(authority.Root, subject, codeSigningEku);

    private static X509Certificate2 IssueLeaf(X509Certificate2 root, string subject, bool codeSigningEku)
    {
        using var leafKey = RSA.Create(2048);
        var request = new CertificateRequest(subject, leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(codeSigningEku ? CodeSigningEku : ServerAuthEku)], true));
        request.CertificateExtensions.Add(new X509AuthorityKeyIdentifierExtension(
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false).RawData));

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;

        using var leafPublic = request.Create(root, notBefore, notBefore.AddYears(2), serial);
        return leafPublic.CopyWithPrivateKey(leafKey);
    }

    /// <summary>Chain policy that trusts exactly the given root and nothing else.</summary>
    public sealed class TrustingChainPolicy(X509Certificate2 root) : IAuthenticodeChainPolicy
    {
        private readonly X509Certificate2 _root = X509CertificateLoader.LoadCertificate(root.RawData);

        public void Apply(X509ChainPolicy policy)
        {
            policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            policy.CustomTrustStore.Clear();
            policy.CustomTrustStore.Add(_root);
        }
    }

    // ---- signatures ---------------------------------------------------------

    /// <summary>An Authenticode-shaped PKCS#7 blob signed by the given leaf.</summary>
    public static byte[] SignatureBlob(X509Certificate2 leaf, X509Certificate2? rootToEmbed = null)
    {
        // The verifier does not parse SpcIndirectDataContent; any DER is fine.
        var content = new ContentInfo(SpcIndirectData, [0x30, 0x00]);
        var cms = new SignedCms(content, detached: false);

        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, leaf)
        {
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));

        cms.ComputeSignature(signer);

        if (rootToEmbed is not null)
        {
            cms.Certificates.Add(X509CertificateLoader.LoadCertificate(rootToEmbed.RawData));
        }

        return cms.Encode();
    }

    public static byte[] SignatureBlob(Authority authority, bool includeRootInBlob = true) =>
        SignatureBlob(authority.Leaf, includeRootInBlob ? authority.Root : null);

    // ---- compound files -----------------------------------------------------

    private const int DefaultSectorSize = 512;

    /// <summary>The 4 KB sectors Windows Installer itself writes, and the built agent MSI has.</summary>
    public const int LargeSectorSize = 4096;
    private const int Mini = 64;
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSector = 0xFFFFFFFF;
    private const uint NoStream = 0xFFFFFFFF;

    /// <summary>A minimal compound file holding one stream (or none).</summary>
    public static byte[] CompoundFile(string? streamName, byte[] payload, uint miniCutoff = 0, int sectorSize = DefaultSectorSize) =>
        streamName is null
            ? CompoundFile([], miniCutoff, sectorSize)
            : CompoundFile([(streamName, payload)], miniCutoff, sectorSize);

    /// <summary>
    /// A minimal compound file holding the given streams.
    /// </summary>
    /// <param name="miniCutoff">
    /// Size below which streams live in the mini stream. Pass 0 to force regular
    /// sectors regardless of size, or the standard 4096 to exercise the mini path.
    /// </param>
    public static byte[] CompoundFile(IReadOnlyList<(string Name, byte[] Payload)> streams, uint miniCutoff = 0, int sectorSize = DefaultSectorSize)
    {
        // The FAT sits at the front, so how many sectors it needs must be known
        // before anything is placed -- and how many are needed depends on what is
        // placed. Laid out with one FAT sector, then again with as many as that
        // layout turned out to need; the second pass is stable, since adding FAT
        // sectors adds one FAT entry each.
        var fatSectors = 1;
        while (true)
        {
            var file = Layout(streams, miniCutoff, sectorSize, fatSectors, out var needed);
            if (needed <= fatSectors)
            {
                return file;
            }

            fatSectors = needed;
        }
    }

    private static byte[] Layout(
        IReadOnlyList<(string Name, byte[] Payload)> streams, uint miniCutoff, int sectorSize, int fatSectors, out int neededFatSectors)
    {
        // sectorSize plan: the FAT, then the directory (four 128-byte entries per
        // sector, root included), then data. An MSI-shaped artifact carries a
        // signature, a seed and three database streams, so the directory spans
        // more than one sector; the reader walks the chain like any other.
        var perDirectorySector = sectorSize / 128;
        var directorySectors = (streams.Count + 1 + perDirectorySector - 1) / perDirectorySector;
        var directoryStart = fatSectors;
        var sectors = new List<byte[]>();
        var fat = new List<uint>();

        void Reserve(int count)
        {
            for (var i = 0; i < count; i++)
            {
                sectors.Add(new byte[sectorSize]);
                fat.Add(FreeSector);
            }
        }

        Reserve(fatSectors + directorySectors);
        for (var s = 0; s < fatSectors; s++)
        {
            fat[s] = 0xFFFFFFFD; // FAT sector marker
        }

        for (var d = 0; d < directorySectors; d++)
        {
            fat[directoryStart + d] = d == directorySectors - 1 ? EndOfChain : (uint)(directoryStart + d + 1);
        }

        // Mini-stream residents are packed into one buffer owned by the root entry.
        var miniResidents = new List<(int Index, byte[] Payload)>();
        var starts = new uint[streams.Count];

        for (var i = 0; i < streams.Count; i++)
        {
            var payload = streams[i].Payload;
            if (payload.Length == 0)
            {
                starts[i] = EndOfChain;
            }
            else if ((uint)payload.Length < miniCutoff)
            {
                miniResidents.Add((i, payload));
            }
            else
            {
                starts[i] = WriteChain(sectors, fat, Reserve, payload, sectorSize);
            }
        }

        uint rootStart = EndOfChain;
        ulong rootSize = 0;
        uint miniFatStart = EndOfChain;
        uint miniFatCount = 0;

        if (miniResidents.Count > 0)
        {
            var miniFat = new List<uint>();
            using var miniStream = new MemoryStream();

            foreach (var (index, payload) in miniResidents)
            {
                var count = (payload.Length + Mini - 1) / Mini;
                starts[index] = (uint)miniFat.Count;
                for (var j = 0; j < count; j++)
                {
                    miniFat.Add(j == count - 1 ? EndOfChain : (uint)(miniFat.Count + 1));
                }

                miniStream.Write(payload);
                miniStream.Write(new byte[count * Mini - payload.Length]);
            }

            var miniBytes = miniStream.ToArray();
            rootStart = WriteChain(sectors, fat, Reserve, miniBytes, sectorSize);
            rootSize = (ulong)miniBytes.Length;

            var miniFatBytes = new byte[sectorSize];
            Array.Fill(miniFatBytes, (byte)0xFF);
            for (var i = 0; i < miniFat.Count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(miniFatBytes.AsSpan(i * 4), miniFat[i]);
            }

            miniFatStart = WriteChain(sectors, fat, Reserve, miniFatBytes, sectorSize);
            miniFatCount = 1;
        }

        // ---- directory --------------------------------------------------------
        for (var d = 0; d < directorySectors; d++)
        {
            var dir = sectors[directoryStart + d];
            for (var e = 0; e < sectorSize / 128; e++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(e * 128 + 0x44), NoStream);
                BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(e * 128 + 0x48), NoStream);
                BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(e * 128 + 0x4C), NoStream);
            }
        }

        WriteEntry(sectors, directoryStart, sectorSize, 0, "Root Entry", type: 5, start: rootStart, size: rootSize, child: streams.Count == 0 ? NoStream : 1);
        for (var i = 0; i < streams.Count; i++)
        {
            WriteEntry(sectors, directoryStart, sectorSize, i + 1, streams[i].Name, type: 2, start: starts[i], size: (ulong)streams[i].Payload.Length, child: NoStream);
        }

        // ---- FAT sectors --------------------------------------------------------
        var entriesPerFatSector = sectorSize / 4;
        neededFatSectors = (fat.Count + entriesPerFatSector - 1) / entriesPerFatSector;
        for (var s = 0; s < fatSectors; s++)
        {
            Array.Fill(sectors[s], (byte)0xFF);
        }

        for (var i = 0; i < fat.Count && i < fatSectors * entriesPerFatSector; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                sectors[i / entriesPerFatSector].AsSpan((i % entriesPerFatSector) * 4), fat[i]);
        }

        // ---- header -----------------------------------------------------------
        var header = new byte[sectorSize];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x18), 0x003E);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1A), (ushort)(sectorSize == 512 ? 3 : 4));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1C), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1E), (ushort)(sectorSize == 512 ? 9 : 12));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x20), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x2C), (uint)fatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x30), (uint)directoryStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x38), miniCutoff);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x3C), miniFatStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x40), miniFatCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x44), EndOfChain);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x48), 0);
        // The header lists the first 109 FAT sectors; this writer never needs more.
        if (fatSectors > 109)
        {
            throw new ArgumentException("The test writer supports at most 109 FAT sectors.", nameof(streams));
        }

        for (var i = 0; i < 109; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x4C + i * 4), i < fatSectors ? (uint)i : FreeSector);
        }

        var file = new byte[sectorSize + sectors.Count * sectorSize];
        header.CopyTo(file, 0);
        for (var i = 0; i < sectors.Count; i++)
        {
            sectors[i].CopyTo(file, sectorSize + i * sectorSize);
        }

        return file;
    }

    // ---- Windows Installer database ------------------------------------------

    /// <summary>
    /// The ProductVersion an artifact carries when a test does not say otherwise.
    /// A release declaring anything else is, correctly, refused.
    /// </summary>
    public const string DefaultProductVersion = "1.0.0";

    /// <summary>The Property rows a real agent MSI carries, around the one that matters.</summary>
    public static IReadOnlyList<(string Property, string Value)> AgentProperties(string productVersion) =>
    [
        ("Manufacturer", "Endpoint Platform"),
        ("ProductCode", "{C3470886-369C-40A5-8019-8A01D2D8DBBA}"),
        ("ProductName", "Endpoint Platform Agent"),
        ("ProductVersion", productVersion),
        ("UpgradeCode", "{8F3C1D92-6B74-4A5E-9D21-7C4E8B0F5A63}"),
    ];

    /// <summary>
    /// The three streams that make a compound file a Windows Installer database
    /// with the given Property table: <c>_StringPool</c>, <c>_StringData</c> and
    /// the <c>Property</c> table, under the names Windows Installer stores them as.
    /// </summary>
    /// <param name="longStringRefs">
    /// Write three-byte string references and set the pool flag that announces
    /// them, as a database with more than 65,535 strings would.
    /// </param>
    /// <param name="codePage">The string data's code page; 1252 is what WiX writes by default.</param>
    public static IReadOnlyList<(string Name, byte[] Payload)> MsiDatabaseStreams(
        IReadOnlyList<(string Property, string Value)> properties,
        bool longStringRefs = false,
        int codePage = 1252)
    {
        var strings = new List<string>();
        int IdOf(string value)
        {
            var index = strings.IndexOf(value);
            if (index < 0)
            {
                strings.Add(value);
                index = strings.Count - 1;
            }

            return index + 1; // 1-based; 0 is the null string
        }

        var rows = properties.Select(p => (Name: IdOf(p.Property), Value: IdOf(p.Value))).ToList();

        var encoding = codePage == 65001 ? Encoding.UTF8 : Encoding.Latin1;
        using var pool = new MemoryStream();
        using var data = new MemoryStream();

        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)(codePage & 0xFFFF));
        BinaryPrimitives.WriteUInt16LittleEndian(word[2..], (ushort)((codePage >> 16) | (longStringRefs ? 0x8000 : 0)));
        pool.Write(word);

        foreach (var value in strings)
        {
            var bytes = encoding.GetBytes(value);
            if (bytes.Length > 0xFFFF)
            {
                // A long string: zero length with a non-zero count, then the real
                // 32-bit length in the next slot.
                BinaryPrimitives.WriteUInt16LittleEndian(word, 0);
                BinaryPrimitives.WriteUInt16LittleEndian(word[2..], 1);
                pool.Write(word);
                BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)bytes.Length);
                pool.Write(word);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)bytes.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(word[2..], 1);
                pool.Write(word);
            }

            data.Write(bytes);
        }

        // Column-major: every Property reference, then every Value reference.
        var width = longStringRefs ? 3 : 2;
        var table = new byte[rows.Count * width * 2];
        for (var r = 0; r < rows.Count; r++)
        {
            WriteReference(table, r * width, rows[r].Name, width);
            WriteReference(table, rows.Count * width + r * width, rows[r].Value, width);
        }

        return
        [
            (MsiDatabase.EncodeStreamName("_StringPool", database: true), pool.ToArray()),
            (MsiDatabase.EncodeStreamName("_StringData", database: true), data.ToArray()),
            (MsiDatabase.EncodeStreamName("Property", database: true), table),
        ];
    }

    private static void WriteReference(byte[] table, int offset, int reference, int width)
    {
        table[offset] = (byte)reference;
        table[offset + 1] = (byte)(reference >> 8);
        if (width == 3)
        {
            table[offset + 2] = (byte)(reference >> 16);
        }
    }

    /// <summary>
    /// An MSI-shaped file signed by the authority, carrying a database whose
    /// ProductVersion is <paramref name="productVersion"/>. <paramref name="seed"/>
    /// goes into a further stream so distinct seeds yield distinct bytes and
    /// content addresses.
    /// </summary>
    public static byte[] SignedMsi(
        Authority authority,
        uint miniCutoff = 0,
        bool includeRootInBlob = true,
        string? seed = null,
        string productVersion = DefaultProductVersion) =>
        SignedMsi(SignatureBlob(authority, includeRootInBlob), miniCutoff, seed, productVersion);

    /// <summary>An MSI-shaped file carrying a signature by an arbitrary leaf.</summary>
    public static byte[] SignedMsi(
        X509Certificate2 leaf, X509Certificate2? rootToEmbed, string? seed = null, string productVersion = DefaultProductVersion) =>
        SignedMsi(SignatureBlob(leaf, rootToEmbed), 0, seed, productVersion);

    private static byte[] SignedMsi(byte[] blob, uint miniCutoff, string? seed, string productVersion)
    {
        var streams = new List<(string, byte[])> { (AuthenticodeVerifier.SignatureStreamName, blob) };
        streams.AddRange(MsiDatabaseStreams(AgentProperties(productVersion)));
        if (seed is not null)
        {
            streams.Add(("Seed", Encoding.UTF8.GetBytes(seed)));
        }

        return CompoundFile(streams, miniCutoff);
    }

    /// <summary>
    /// An MSI-shaped file with no signature stream at all, carrying a database
    /// whose ProductVersion is <paramref name="productVersion"/>.
    /// </summary>
    public static byte[] UnsignedMsi(string? seed = null, string productVersion = DefaultProductVersion) =>
        MsiWithProperties(AgentProperties(productVersion), seed);

    /// <summary>An unsigned MSI-shaped file with exactly these Property rows.</summary>
    public static byte[] MsiWithProperties(
        IReadOnlyList<(string Property, string Value)> properties,
        string? seed = null,
        bool longStringRefs = false,
        int codePage = 1252,
        uint miniCutoff = 0,
        int sectorSize = DefaultSectorSize)
    {
        var streams = new List<(string, byte[])> { ("SummaryInformation", Encoding.ASCII.GetBytes("not a signature")) };
        streams.AddRange(MsiDatabaseStreams(properties, longStringRefs, codePage));
        if (seed is not null)
        {
            streams.Add(("Seed", Encoding.UTF8.GetBytes(seed)));
        }

        return CompoundFile(streams, miniCutoff, sectorSize);
    }

    /// <summary>
    /// A compound file that is an MSI in shape only: no database streams at all.
    /// What every artifact this writer produced looked like before the
    /// ProductVersion gate existed.
    /// </summary>
    public static byte[] MsiWithoutDatabase(string? seed = null)
    {
        var streams = new List<(string, byte[])> { ("SummaryInformation", Encoding.ASCII.GetBytes("not a signature")) };
        if (seed is not null)
        {
            streams.Add(("Seed", Encoding.UTF8.GetBytes(seed)));
        }

        return CompoundFile(streams);
    }

    /// <summary>
    /// An unsigned MSI-shaped file of at least <paramref name="totalBytes"/>,
    /// laid out the way Windows Installer lays out a large package: 4 KB
    /// sectors, the database's small streams in the mini stream, and the bulk in
    /// one patterned "Padding" stream -- fast to build, and content identity is
    /// the hash.
    /// </summary>
    public static byte[] OversizedMsi(int totalBytes, byte seed, string productVersion)
    {
        var padding = new byte[totalBytes];
        for (var i = 0; i < padding.Length; i++)
        {
            padding[i] = (byte)(seed + (i % 251));
        }

        var streams = new List<(string, byte[])>();
        streams.AddRange(MsiDatabaseStreams(AgentProperties(productVersion)));
        streams.Add(("Padding", padding));

        return CompoundFile(streams, miniCutoff: 4096, sectorSize: LargeSectorSize);
    }

    private static uint WriteChain(List<byte[]> sectors, List<uint> fat, Action<int> reserve, byte[] data, int sectorSize)
    {
        var count = Math.Max(1, (data.Length + sectorSize - 1) / sectorSize);
        var first = (uint)sectors.Count;
        reserve(count);

        for (var i = 0; i < count; i++)
        {
            var index = (int)first + i;
            var take = Math.Min(sectorSize, data.Length - i * sectorSize);
            if (take > 0)
            {
                data.AsSpan(i * sectorSize, take).CopyTo(sectors[index]);
            }

            fat[index] = i == count - 1 ? EndOfChain : (uint)(index + 1);
        }

        return first;
    }

    private static void WriteEntry(
        List<byte[]> sectors, int directoryStart, int sectorSize, int index, string name, byte type, uint start, ulong size, uint child)
    {
        var perSector = sectorSize / 128;
        var entry = sectors[directoryStart + index / perSector].AsSpan((index % perSector) * 128, 128);
        var nameBytes = Encoding.Unicode.GetBytes(name);
        nameBytes.CopyTo(entry);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[0x40..], (ushort)(nameBytes.Length + 2));
        entry[0x42] = type;
        entry[0x43] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(entry[0x4C..], child);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[0x74..], start);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[0x78..], size);
    }
}
