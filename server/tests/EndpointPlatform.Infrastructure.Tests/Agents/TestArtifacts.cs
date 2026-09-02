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
/// The compound-file writer is the minimum that produces a file the reader must
/// handle: v3 layout, one FAT sector, one directory sector, and streams in either
/// regular sectors or the mini stream depending on the size cutoff requested.
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

    private const int Sector = 512;
    private const int Mini = 64;
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSector = 0xFFFFFFFF;
    private const uint NoStream = 0xFFFFFFFF;

    /// <summary>A minimal compound file holding one stream (or none).</summary>
    public static byte[] CompoundFile(string? streamName, byte[] payload, uint miniCutoff = 0) =>
        streamName is null
            ? CompoundFile([], miniCutoff)
            : CompoundFile([(streamName, payload)], miniCutoff);

    /// <summary>
    /// A minimal compound file holding the given streams.
    /// </summary>
    /// <param name="miniCutoff">
    /// Size below which streams live in the mini stream. Pass 0 to force regular
    /// sectors regardless of size, or the standard 4096 to exercise the mini path.
    /// </param>
    public static byte[] CompoundFile(IReadOnlyList<(string Name, byte[] Payload)> streams, uint miniCutoff = 0)
    {
        if (streams.Count > 3)
        {
            throw new ArgumentException("The test writer supports at most three streams.", nameof(streams));
        }

        // Sector plan: 0 = FAT, 1 = directory, then data.
        var sectors = new List<byte[]>();
        var fat = new List<uint>();

        void Reserve(int count)
        {
            for (var i = 0; i < count; i++)
            {
                sectors.Add(new byte[Sector]);
                fat.Add(FreeSector);
            }
        }

        Reserve(2);
        fat[0] = 0xFFFFFFFD; // FAT sector marker
        fat[1] = EndOfChain;

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
                starts[i] = WriteChain(sectors, fat, Reserve, payload);
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
            rootStart = WriteChain(sectors, fat, Reserve, miniBytes);
            rootSize = (ulong)miniBytes.Length;

            var miniFatBytes = new byte[Sector];
            Array.Fill(miniFatBytes, (byte)0xFF);
            for (var i = 0; i < miniFat.Count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(miniFatBytes.AsSpan(i * 4), miniFat[i]);
            }

            miniFatStart = WriteChain(sectors, fat, Reserve, miniFatBytes);
            miniFatCount = 1;
        }

        // ---- directory --------------------------------------------------------
        var dir = sectors[1];
        for (var e = 0; e < Sector / 128; e++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(e * 128 + 0x44), NoStream);
            BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(e * 128 + 0x48), NoStream);
            BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(e * 128 + 0x4C), NoStream);
        }

        WriteEntry(dir, 0, "Root Entry", type: 5, start: rootStart, size: rootSize, child: streams.Count == 0 ? NoStream : 1);
        for (var i = 0; i < streams.Count; i++)
        {
            WriteEntry(dir, i + 1, streams[i].Name, type: 2, start: starts[i], size: (ulong)streams[i].Payload.Length, child: NoStream);
        }

        // ---- FAT sector -------------------------------------------------------
        var fatBytes = sectors[0];
        Array.Fill(fatBytes, (byte)0xFF);
        for (var i = 0; i < fat.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fatBytes.AsSpan(i * 4), fat[i]);
        }

        // ---- header -----------------------------------------------------------
        var header = new byte[Sector];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x18), 0x003E);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1A), 0x0003);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1C), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1E), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x20), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x2C), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x30), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x38), miniCutoff);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x3C), miniFatStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x40), miniFatCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x44), EndOfChain);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x48), 0);
        for (var i = 0; i < 109; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x4C + i * 4), i == 0 ? 0u : FreeSector);
        }

        var file = new byte[Sector + sectors.Count * Sector];
        header.CopyTo(file, 0);
        for (var i = 0; i < sectors.Count; i++)
        {
            sectors[i].CopyTo(file, Sector + i * Sector);
        }

        return file;
    }

    /// <summary>
    /// An MSI-shaped file signed by the authority. <paramref name="seed"/> goes into
    /// a second stream so distinct seeds yield distinct bytes and content addresses.
    /// </summary>
    public static byte[] SignedMsi(Authority authority, uint miniCutoff = 0, bool includeRootInBlob = true, string? seed = null) =>
        SignedMsi(SignatureBlob(authority, includeRootInBlob), miniCutoff, seed);

    /// <summary>An MSI-shaped file carrying a signature by an arbitrary leaf.</summary>
    public static byte[] SignedMsi(X509Certificate2 leaf, X509Certificate2? rootToEmbed, string? seed = null) =>
        SignedMsi(SignatureBlob(leaf, rootToEmbed), 0, seed);

    private static byte[] SignedMsi(byte[] blob, uint miniCutoff, string? seed)
    {
        var streams = new List<(string, byte[])> { (AuthenticodeVerifier.SignatureStreamName, blob) };
        if (seed is not null)
        {
            streams.Add(("Seed", Encoding.UTF8.GetBytes(seed)));
        }

        return CompoundFile(streams, miniCutoff);
    }

    /// <summary>An MSI-shaped file with no signature stream at all.</summary>
    public static byte[] UnsignedMsi(string? seed = null)
    {
        var streams = new List<(string, byte[])> { ("SummaryInformation", Encoding.ASCII.GetBytes("not a signature")) };
        if (seed is not null)
        {
            streams.Add(("Seed", Encoding.UTF8.GetBytes(seed)));
        }

        return CompoundFile(streams);
    }

    private static uint WriteChain(List<byte[]> sectors, List<uint> fat, Action<int> reserve, byte[] data)
    {
        var count = Math.Max(1, (data.Length + Sector - 1) / Sector);
        var first = (uint)sectors.Count;
        reserve(count);

        for (var i = 0; i < count; i++)
        {
            var index = (int)first + i;
            var take = Math.Min(Sector, data.Length - i * Sector);
            if (take > 0)
            {
                data.AsSpan(i * Sector, take).CopyTo(sectors[index]);
            }

            fat[index] = i == count - 1 ? EndOfChain : (uint)(index + 1);
        }

        return first;
    }

    private static void WriteEntry(byte[] dir, int index, string name, byte type, uint start, ulong size, uint child)
    {
        var entry = dir.AsSpan(index * 128, 128);
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
