using System.Security.Cryptography;
using System.Text;
using EndpointPlatform.Infrastructure.Agents;

namespace EndpointPlatform.Infrastructure.Tests.Agents;

/// <summary>
/// Reading a ProductVersion out of a Windows Installer database without Windows.
/// </summary>
/// <remarks>
/// <para>
/// The generated databases here follow the format as specified; the one WiX
/// actually produces is checked by <see cref="RealAgentMsiTests"/> when a built
/// package is available. Between them: the stream names are the ones Windows
/// Installer uses, the string pool decodes in both reference widths and both
/// code pages, and every way the data can be absent or broken answers with a
/// named outcome rather than an exception or a guess.
/// </para>
/// </remarks>
public sealed class MsiDatabaseTests
{
    // ---- stream names ----------------------------------------------------------

    /// <summary>
    /// The encoded names are what the directory of a real MSI holds. Derived by
    /// hand from the alphabet, independently of the code, so a wrong table in
    /// one is caught by the other -- and the string pool's marker confirmed
    /// against a package WiX built, where the specification alone had not been
    /// enough.
    /// </summary>
    [Theory]
    [InlineData("_StringPool", true, "䡀㼿䕷䑬㹪䒲䠯")]
    [InlineData("_StringData", true, "䡀㼿䕷䑬㭪䗤䠤")]
    [InlineData("Property", true, "䡀䕙䓲䕨䜷")]
    public void Encodes_stream_names_as_windows_installer_stores_them(string name, bool database, string expected)
    {
        MsiDatabase.EncodeStreamName(name, database).ShouldBe(expected);
    }

    [Fact]
    public void A_character_outside_the_alphabet_is_stored_as_itself()
    {
        // '-' is not in the alphabet; 'a' and 'b' are, but not adjacent to each other.
        MsiDatabase.EncodeStreamName("a-b", database: false).ShouldBe("䠤-䠥");
    }

    // ---- reading ---------------------------------------------------------------

    [Fact]
    public void Reads_the_product_version_from_a_generated_database()
    {
        var msi = TestArtifacts.UnsignedMsi(productVersion: "1.7.0");

        MsiDatabase.TryReadProductVersion(msi).ShouldBe(MsiProductVersion.Found("1.7.0"));
    }

    /// <summary>The small streams of a real MSI live in the mini stream; the reader must follow them there.</summary>
    [Fact]
    public void Reads_a_database_whose_streams_live_in_the_mini_stream()
    {
        var msi = TestArtifacts.MsiWithProperties(TestArtifacts.AgentProperties("2.3.4"), miniCutoff: 4096);

        MsiDatabase.TryReadProductVersion(msi).ShouldBe(MsiProductVersion.Found("2.3.4"));
    }

    [Fact]
    public void Reads_a_database_with_three_byte_string_references()
    {
        var msi = TestArtifacts.MsiWithProperties(TestArtifacts.AgentProperties("3.0.1"), longStringRefs: true);

        MsiDatabase.TryReadProductVersion(msi).ShouldBe(MsiProductVersion.Found("3.0.1"));
    }

    /// <summary>
    /// The layout Windows Installer itself writes, and the built agent MSI has:
    /// 4 KB sectors, with the database's small streams in the mini stream. The
    /// 512-byte artifacts everywhere else never exercised this.
    /// </summary>
    [Fact]
    public void Reads_a_v4_database_with_4kb_sectors()
    {
        var msi = TestArtifacts.MsiWithProperties(
            TestArtifacts.AgentProperties("8.0.0"), miniCutoff: 4096, sectorSize: TestArtifacts.LargeSectorSize);

        MsiDatabase.TryReadProductVersion(msi).ShouldBe(MsiProductVersion.Found("8.0.0"));
    }

    /// <summary>
    /// The built agent MSI ends 3,085 bytes into its final 4 KB sector, and the
    /// string pool is what lives there. A reader that refuses a sector it cannot
    /// read in full reports that package as having no database at all.
    /// </summary>
    [Fact]
    public void Reads_a_database_whose_final_sector_is_short()
    {
        var streams = TestArtifacts.MsiDatabaseStreams(TestArtifacts.AgentProperties("8.0.1"));
        var file = TestArtifacts.CompoundFile(streams, miniCutoff: 0, sectorSize: TestArtifacts.LargeSectorSize);

        // The last stream written is the Property table, so the file's last
        // sector is its last sector. Drop the zero padding after it so the file
        // ends where the data does, as Windows Installer writes it.
        var tail = streams[^1].Payload.Length % TestArtifacts.LargeSectorSize;
        var unpadded = file[..(file.Length - (TestArtifacts.LargeSectorSize - tail))];

        MsiDatabase.TryReadProductVersion(unpadded).ShouldBe(MsiProductVersion.Found("8.0.1"));
    }

    [Fact]
    public void Reads_a_utf8_database()
    {
        (string, string)[] properties =
        [
            ("ProductName", "Agent — édition interne"),
            ("ProductVersion", "4.5.6"),
        ];
        var msi = TestArtifacts.MsiWithProperties(properties, codePage: 65001);

        MsiDatabase.TryReadProductVersion(msi).ShouldBe(MsiProductVersion.Found("4.5.6"));
    }

    /// <summary>A string over 64 KB uses the long-length encoding; the ones after it must still be found.</summary>
    [Fact]
    public void Reads_past_a_long_string_in_the_pool()
    {
        (string, string)[] properties =
        [
            ("ARPCOMMENTS", new string('c', 70_000)),
            ("ProductVersion", "5.0.0"),
        ];
        var msi = TestArtifacts.MsiWithProperties(properties);

        MsiDatabase.TryReadProductVersion(msi).ShouldBe(MsiProductVersion.Found("5.0.0"));
    }

    [Fact]
    public void Finds_the_row_wherever_it_sits_in_the_table()
    {
        (string, string)[] properties =
        [
            ("ProductVersion", "6.0.0"),
            ("ProductName", "First row"),
        ];

        MsiDatabase.TryReadProductVersion(TestArtifacts.MsiWithProperties(properties))
            .ShouldBe(MsiProductVersion.Found("6.0.0"));
    }

    [Fact]
    public void Trims_the_value()
    {
        var msi = TestArtifacts.MsiWithProperties([("ProductVersion", "  7.0.0 ")]);

        MsiDatabase.TryReadProductVersion(msi).ShouldBe(MsiProductVersion.Found("7.0.0"));
    }

    // ---- absence, each named ---------------------------------------------------

    [Fact]
    public void A_compound_file_with_no_database_has_no_string_pool()
    {
        MsiDatabase.TryReadProductVersion(TestArtifacts.MsiWithoutDatabase("x"))
            .ShouldBe(MsiProductVersion.Absent(MsiProductVersionOutcome.NoStringPool));
    }

    [Fact]
    public void Bytes_that_are_not_a_compound_file_have_no_string_pool()
    {
        MsiDatabase.TryReadProductVersion(Encoding.ASCII.GetBytes("MZ not an msi at all"))
            .ShouldBe(MsiProductVersion.Absent(MsiProductVersionOutcome.NoStringPool));
        MsiDatabase.TryReadProductVersion(Array.Empty<byte>())
            .ShouldBe(MsiProductVersion.Absent(MsiProductVersionOutcome.NoStringPool));
    }

    [Fact]
    public void A_database_without_a_property_table_says_so()
    {
        var streams = TestArtifacts.MsiDatabaseStreams(TestArtifacts.AgentProperties("1.0.0"))
            .Where(s => s.Name != MsiDatabase.EncodeStreamName("Property", database: true))
            .ToList();
        var msi = TestArtifacts.CompoundFile(streams);

        MsiDatabase.TryReadProductVersion(msi)
            .ShouldBe(MsiProductVersion.Absent(MsiProductVersionOutcome.NoPropertyTable));
    }

    [Fact]
    public void A_property_table_without_a_product_version_row_is_not_declared()
    {
        var msi = TestArtifacts.MsiWithProperties([("ProductName", "No version here"), ("UpgradeCode", "{X}")]);

        MsiDatabase.TryReadProductVersion(msi)
            .ShouldBe(MsiProductVersion.Absent(MsiProductVersionOutcome.NotDeclared));
    }

    [Fact]
    public void An_empty_product_version_is_not_declared()
    {
        var msi = TestArtifacts.MsiWithProperties([("ProductVersion", "   ")]);

        MsiDatabase.TryReadProductVersion(msi)
            .ShouldBe(MsiProductVersion.Absent(MsiProductVersionOutcome.NotDeclared));
    }

    // ---- hostile input ---------------------------------------------------------

    [Fact]
    public void A_string_pool_of_the_wrong_length_is_malformed()
    {
        var streams = TestArtifacts.MsiDatabaseStreams(TestArtifacts.AgentProperties("1.0.0")).ToList();
        var pool = streams.FindIndex(s => s.Name == MsiDatabase.EncodeStreamName("_StringPool", database: true));
        streams[pool] = (streams[pool].Name, streams[pool].Payload[..^1]);

        MsiDatabase.TryReadProductVersion(TestArtifacts.CompoundFile(streams))
            .ShouldBe(MsiProductVersion.Absent(MsiProductVersionOutcome.Malformed));
    }

    [Fact]
    public void A_pool_whose_lengths_exceed_the_data_is_malformed()
    {
        var streams = TestArtifacts.MsiDatabaseStreams(TestArtifacts.AgentProperties("1.0.0")).ToList();
        var data = streams.FindIndex(s => s.Name == MsiDatabase.EncodeStreamName("_StringData", database: true));
        streams[data] = (streams[data].Name, streams[data].Payload[..8]);

        MsiDatabase.TryReadProductVersion(TestArtifacts.CompoundFile(streams))
            .ShouldBe(MsiProductVersion.Absent(MsiProductVersionOutcome.Malformed));
    }

    [Fact]
    public void A_property_table_of_the_wrong_length_is_malformed()
    {
        var streams = TestArtifacts.MsiDatabaseStreams(TestArtifacts.AgentProperties("1.0.0")).ToList();
        var table = streams.FindIndex(s => s.Name == MsiDatabase.EncodeStreamName("Property", database: true));
        streams[table] = (streams[table].Name, streams[table].Payload[..^1]);

        MsiDatabase.TryReadProductVersion(TestArtifacts.CompoundFile(streams))
            .ShouldBe(MsiProductVersion.Absent(MsiProductVersionOutcome.Malformed));
    }

    [Fact]
    public void A_reference_past_the_end_of_the_pool_is_skipped_not_followed()
    {
        var streams = TestArtifacts.MsiDatabaseStreams([("ProductVersion", "1.0.0")]).ToList();
        var table = streams.FindIndex(s => s.Name == MsiDatabase.EncodeStreamName("Property", database: true));
        var payload = (byte[])streams[table].Payload.Clone();
        payload[0] = 0xFF;
        payload[1] = 0xFF; // Property reference 65535: there is no such string.
        streams[table] = (streams[table].Name, payload);

        MsiDatabase.TryReadProductVersion(TestArtifacts.CompoundFile(streams))
            .ShouldBe(MsiProductVersion.Absent(MsiProductVersionOutcome.NotDeclared));
    }

    [Fact]
    public void Invalid_utf8_in_a_utf8_database_is_malformed_not_repaired()
    {
        var streams = TestArtifacts.MsiDatabaseStreams([("ProductVersion", "1.0.0")], codePage: 65001).ToList();
        var data = streams.FindIndex(s => s.Name == MsiDatabase.EncodeStreamName("_StringData", database: true));
        var payload = (byte[])streams[data].Payload.Clone();
        payload[0] = 0xC3; // a lead byte with no continuation
        payload[1] = 0x28;
        streams[data] = (streams[data].Name, payload);

        MsiDatabase.TryReadProductVersion(TestArtifacts.CompoundFile(streams))
            .ShouldBe(MsiProductVersion.Absent(MsiProductVersionOutcome.Malformed));
    }

    [Fact]
    public void Random_bytes_never_throw()
    {
        for (var i = 0; i < 50; i++)
        {
            var bytes = RandomNumberGenerator.GetBytes(4096);
            new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(bytes, 0); // wears the magic

            Should.NotThrow(() => MsiDatabase.TryReadProductVersion(bytes));
        }
    }
}

/// <summary>
/// The reader against a package WiX actually built. Skipped, and reported as
/// skipped, when no package is provided.
/// </summary>
public sealed class RealAgentMsiTests
{
    [RealAgentMsiFact]
    public async Task Reads_the_product_version_of_the_real_agent_package()
    {
        var bytes = await File.ReadAllBytesAsync(RealAgentMsiFactAttribute.Path!);

        var product = MsiDatabase.TryReadProductVersion(bytes);

        product.IsFound.ShouldBeTrue(product.Outcome.ToString());
        Domain.Agents.AgentVersionNumber.TryParse(product.Value, out _).ShouldBeTrue(product.Value);
    }

    [RealAgentMsiFact]
    public async Task The_staged_1_7_0_package_is_1_7_0()
    {
        var bytes = await File.ReadAllBytesAsync(RealAgentMsiFactAttribute.Path!);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));

        if (sha256 != RealAgentMsiFactAttribute.Agent170Sha256)
        {
            // Some other build was supplied; the previous test covers it.
            return;
        }

        MsiDatabase.TryReadProductVersion(bytes).ShouldBe(MsiProductVersion.Found("1.7.0"));
    }
}
