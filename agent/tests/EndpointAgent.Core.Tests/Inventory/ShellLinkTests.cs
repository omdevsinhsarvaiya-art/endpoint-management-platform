using System.Buffers.Binary;
using System.Text;
using EndpointAgent.Core.Inventory;

namespace EndpointAgent.Core.Tests.Inventory;

/// <summary>
/// Reading a Windows shortcut from its bytes.
/// </summary>
/// <remarks>
/// Fixtures are built here, field by field, so each rule can be exercised
/// alone; one real shortcut Windows wrote pins the whole against the actual
/// format. A shortcut is an untrusted file, so the malformed cases are not edge
/// cases but the point: every one must yield null and none may throw.
/// </remarks>
public sealed class ShellLinkTests
{
    // ---- well-formed --------------------------------------------------------------------

    [Fact]
    public void Reads_the_local_path_from_link_info()
    {
        var bytes = new LinkBuilder().WithLocalPath(@"C:\Program Files\Contoso\app.exe").Build();

        var link = ShellLink.Parse(bytes).ShouldNotBeNull();

        link.LocalPath.ShouldBe(@"C:\Program Files\Contoso\app.exe");
        link.IsAdvertised.ShouldBeFalse();
        link.EnvironmentPath.ShouldBeNull();
    }

    [Fact]
    public void Appends_the_common_path_suffix()
    {
        var bytes = new LinkBuilder().WithLocalPath(@"C:\Program Files\Contoso\", suffix: "app.exe").Build();

        ShellLink.Parse(bytes).ShouldNotBeNull().LocalPath.ShouldBe(@"C:\Program Files\Contoso\app.exe");
    }

    /// <summary>
    /// Windows writes the Unicode form alongside the single-byte one when the path
    /// needs it. The Unicode form is the truth in that case.
    /// </summary>
    [Fact]
    public void Prefers_the_unicode_local_path_when_the_file_carries_one()
    {
        var bytes = new LinkBuilder()
            .WithLocalPath(@"C:\Users\Rene\app.exe", unicodeBasePath: @"C:\Users\René\app.exe")
            .Build();

        ShellLink.Parse(bytes).ShouldNotBeNull().LocalPath.ShouldBe(@"C:\Users\René\app.exe");
    }

    [Fact]
    public void A_link_without_a_local_base_path_has_no_local_path()
    {
        var bytes = new LinkBuilder().WithLocalPath(@"C:\x\app.exe", volumeIdAndLocalBasePath: false).Build();

        ShellLink.Parse(bytes).ShouldNotBeNull().LocalPath.ShouldBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reads_the_string_data_in_either_encoding(bool unicode)
    {
        var bytes = new LinkBuilder(unicode)
            .WithLocalPath(@"C:\x\app.exe")
            .WithDescription("Contoso App")
            .WithRelativePath(@"..\app.exe")
            .WithWorkingDirectory(@"C:\x")
            .WithArguments("--flag")
            .WithIconLocation(@"C:\x\app.exe")
            .Build();

        var link = ShellLink.Parse(bytes).ShouldNotBeNull();

        link.Description.ShouldBe("Contoso App");
        link.RelativePath.ShouldBe(@"..\app.exe");
        link.WorkingDirectory.ShouldBe(@"C:\x");
        link.Arguments.ShouldBe("--flag");
        link.LocalPath.ShouldBe(@"C:\x\app.exe");
    }

    [Fact]
    public void Reads_the_environment_variable_target()
    {
        var bytes = new LinkBuilder()
            .WithEnvironmentTarget(@"%ProgramFiles%\Contoso\app.exe")
            .Build();

        var link = ShellLink.Parse(bytes).ShouldNotBeNull();

        link.EnvironmentPath.ShouldBe(@"%ProgramFiles%\Contoso\app.exe");
        link.LocalPath.ShouldBeNull();
    }

    [Fact]
    public void Marks_a_windows_installer_advertisement()
    {
        var bytes = new LinkBuilder().WithLocalPath(@"C:\x\app.exe").WithDarwinBlock().Build();

        ShellLink.Parse(bytes).ShouldNotBeNull().IsAdvertised.ShouldBeTrue();
    }

    [Fact]
    public void Skips_the_target_id_list_without_interpreting_it()
    {
        var bytes = new LinkBuilder()
            .WithIdList([0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x00])
            .WithLocalPath(@"C:\x\app.exe")
            .Build();

        ShellLink.Parse(bytes).ShouldNotBeNull().LocalPath.ShouldBe(@"C:\x\app.exe");
    }

    [Fact]
    public void Unknown_extra_data_blocks_are_stepped_over()
    {
        var bytes = new LinkBuilder()
            .WithLocalPath(@"C:\x\app.exe")
            .WithExtraBlock(0xA0000009, 32) // PropertyStoreDataBlock, contents irrelevant
            .WithDarwinBlock()
            .Build();

        var link = ShellLink.Parse(bytes).ShouldNotBeNull();

        link.LocalPath.ShouldBe(@"C:\x\app.exe");
        link.IsAdvertised.ShouldBeTrue();
    }

    /// <summary>
    /// The one shortcut here that Windows itself wrote: the Brave installer's
    /// Start Menu entry, copied byte for byte. If the reader agrees with the
    /// specification but not with Windows, this is where it shows.
    /// </summary>
    [Fact]
    public void Reads_a_shortcut_windows_wrote()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "brave.lnk"));

        var link = ShellLink.Parse(bytes).ShouldNotBeNull();

        link.LocalPath.ShouldNotBeNull();
        link.LocalPath.ShouldStartWith(@"C:\Program Files\BraveSoftware\Brave-Browser\Application", Case.Insensitive);
        link.LocalPath.ShouldEndWith(@"\brave.exe", Case.Insensitive);
        link.IsAdvertised.ShouldBeFalse();
        link.Arguments.ShouldBeNull();
        link.WorkingDirectory.ShouldNotBeNull();
        link.RelativePath.ShouldNotBeNull();
    }

    // ---- malformed -----------------------------------------------------------------------

    [Fact]
    public void Empty_and_short_input_is_not_a_link()
    {
        ShellLink.Parse([]).ShouldBeNull();
        ShellLink.Parse(new byte[10]).ShouldBeNull();
        ShellLink.Parse(new byte[0x4B]).ShouldBeNull();
    }

    [Fact]
    public void A_wrong_header_size_or_class_id_is_not_a_link()
    {
        var wrongSize = new LinkBuilder().WithLocalPath(@"C:\x\app.exe").Build();
        BinaryPrimitives.WriteUInt32LittleEndian(wrongSize, 0x4D);
        ShellLink.Parse(wrongSize).ShouldBeNull();

        var wrongClsid = new LinkBuilder().WithLocalPath(@"C:\x\app.exe").Build();
        wrongClsid[4] ^= 0xFF;
        ShellLink.Parse(wrongClsid).ShouldBeNull();
    }

    [Fact]
    public void A_truncated_link_is_refused_at_every_length()
    {
        var whole = new LinkBuilder()
            .WithIdList([1, 2, 3, 4])
            .WithLocalPath(@"C:\Program Files\Contoso\app.exe")
            .WithDescription("Contoso")
            .WithEnvironmentTarget(@"%ProgramFiles%\Contoso\app.exe")
            .Build();

        // Every prefix either parses to something or to null; none may throw. The
        // header alone and anything shorter is never a link.
        for (var length = 0; length < whole.Length; length++)
        {
            var link = Should.NotThrow(() => ShellLink.Parse(whole.AsSpan(0, length).ToArray()));
            if (length < 0x4C)
            {
                link.ShouldBeNull($"length {length}");
            }
        }
    }

    [Fact]
    public void An_id_list_that_runs_past_the_end_is_refused()
    {
        var bytes = new LinkBuilder().WithIdList([1, 2, 3, 4]).WithLocalPath(@"C:\x\app.exe").Build();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x4C), 0xFFFF);

        ShellLink.Parse(bytes).ShouldBeNull();
    }

    [Theory]
    [InlineData(0x00000000u)]
    [InlineData(0x0000001Bu)]
    [InlineData(0x7FFFFFFFu)]
    [InlineData(0xFFFFFFFFu)]
    public void A_link_info_size_that_does_not_fit_is_refused(uint size)
    {
        var bytes = new LinkBuilder().WithLocalPath(@"C:\x\app.exe").Build();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x4C), size);

        ShellLink.Parse(bytes).ShouldBeNull();
    }

    [Fact]
    public void A_local_path_offset_outside_link_info_is_refused()
    {
        var bytes = new LinkBuilder().WithLocalPath(@"C:\x\app.exe").Build();
        // LocalBasePathOffset sits 16 bytes into LinkInfo, which starts at 0x4C.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x4C + 16), 0xFFFFFFF0);

        ShellLink.Parse(bytes).ShouldBeNull();
    }

    [Fact]
    public void A_string_that_runs_past_the_end_is_refused()
    {
        var bytes = new LinkBuilder().WithLocalPath(@"C:\x\app.exe").WithDescription("Contoso").Build();
        var linkInfoSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x4C));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x4C + (int)linkInfoSize), 0x0FFF);

        ShellLink.Parse(bytes).ShouldBeNull();
    }

    [Fact]
    public void An_extra_data_block_that_runs_past_the_end_is_refused()
    {
        var bytes = new LinkBuilder().WithLocalPath(@"C:\x\app.exe").WithDarwinBlock().Build();
        var linkInfoSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x4C));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x4C + (int)linkInfoSize), 0x00010000);

        ShellLink.Parse(bytes).ShouldBeNull();
    }

    [Fact]
    public void Random_bytes_behind_a_valid_header_never_throw()
    {
        var random = new Random(20260908);
        for (var round = 0; round < 500; round++)
        {
            var bytes = new byte[random.Next(0x4C, 2048)];
            random.NextBytes(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x4C);
            new byte[] { 0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 }
                .CopyTo(bytes, 4);

            Should.NotThrow(() => ShellLink.Parse(bytes));
        }
    }

    // ---- environment expansion -------------------------------------------------------

    private static readonly Dictionary<string, string> Variables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ProgramFiles"] = @"C:\Program Files",
        ["USERPROFILE"] = @"C:\Users\Techsara",
    };

    private static string? Lookup(string name) => Variables.TryGetValue(name, out var value) ? value : null;

    [Theory]
    [InlineData(@"%ProgramFiles%\Contoso\app.exe", @"C:\Program Files\Contoso\app.exe")]
    [InlineData(@"%programfiles%\Contoso\app.exe", @"C:\Program Files\Contoso\app.exe")]
    [InlineData(@"%USERPROFILE%\AppData\Local\app.exe", @"C:\Users\Techsara\AppData\Local\app.exe")]
    [InlineData(@"C:\plain\app.exe", @"C:\plain\app.exe")]
    [InlineData(@"C:\100%%\app.exe", @"C:\100%\app.exe")]
    public void Expands_known_variables(string raw, string expected)
    {
        ShellLink.ExpandEnvironment(raw, Lookup).ShouldBe(expected);
    }

    [Theory]
    [InlineData(@"%LOCALAPPDATA%\app.exe")]
    [InlineData(@"%ProgramFiles\app.exe")]
    [InlineData(@"%ProgramFiles%\%Unknown%\app.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_or_unmatched_variable_refuses_the_whole_path(string? raw)
    {
        ShellLink.ExpandEnvironment(raw, Lookup).ShouldBeNull();
    }

    // ---- fixture builder -------------------------------------------------------------------

    /// <summary>Builds shell link bytes one section at a time, per MS-SHLLINK.</summary>
    private sealed class LinkBuilder(bool unicode = true)
    {
        private const uint HasLinkTargetIdList = 0x01;
        private const uint HasLinkInfo = 0x02;
        private const uint HasName = 0x04;
        private const uint HasRelativePath = 0x08;
        private const uint HasWorkingDir = 0x10;
        private const uint HasArguments = 0x20;
        private const uint HasIconLocation = 0x40;
        private const uint IsUnicode = 0x80;

        private uint _flags = unicode ? IsUnicode : 0;
        private byte[]? _idList;
        private byte[]? _linkInfo;
        private string? _description;
        private string? _relativePath;
        private string? _workingDirectory;
        private string? _arguments;
        private string? _iconLocation;
        private readonly List<byte[]> _extra = [];

        public LinkBuilder WithIdList(byte[] data)
        {
            _flags |= HasLinkTargetIdList;
            _idList = data;
            return this;
        }

        public LinkBuilder WithLocalPath(
            string basePath, string suffix = "", string? unicodeBasePath = null, bool volumeIdAndLocalBasePath = true)
        {
            _flags |= HasLinkInfo;

            var headerSize = unicodeBasePath is null ? 0x1C : 0x24;
            var volume = new byte[] { 0x11, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0x10, 0, 0, 0, 0 };
            var baseBytes = Encoding.Latin1.GetBytes(basePath + "\0");
            var suffixBytes = Encoding.Latin1.GetBytes(suffix + "\0");
            var unicodeBytes = unicodeBasePath is null ? [] : Encoding.Unicode.GetBytes(unicodeBasePath + "\0");

            var volumeOffset = headerSize;
            var baseOffset = volumeOffset + volume.Length;
            var suffixOffset = baseOffset + baseBytes.Length;
            var unicodeOffset = suffixOffset + suffixBytes.Length;
            var total = unicodeOffset + unicodeBytes.Length;

            var info = new byte[total];
            var span = info.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)total);
            BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)headerSize);
            BinaryPrimitives.WriteUInt32LittleEndian(span[8..], volumeIdAndLocalBasePath ? 1u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)volumeOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)baseOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(span[20..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)suffixOffset);
            if (unicodeBasePath is not null)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)unicodeOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(span[32..], 0);
            }

            volume.CopyTo(span[volumeOffset..]);
            baseBytes.CopyTo(span[baseOffset..]);
            suffixBytes.CopyTo(span[suffixOffset..]);
            unicodeBytes.CopyTo(span[unicodeOffset..]);

            _linkInfo = info;
            return this;
        }

        public LinkBuilder WithDescription(string value) { _flags |= HasName; _description = value; return this; }
        public LinkBuilder WithRelativePath(string value) { _flags |= HasRelativePath; _relativePath = value; return this; }
        public LinkBuilder WithWorkingDirectory(string value) { _flags |= HasWorkingDir; _workingDirectory = value; return this; }
        public LinkBuilder WithArguments(string value) { _flags |= HasArguments; _arguments = value; return this; }
        public LinkBuilder WithIconLocation(string value) { _flags |= HasIconLocation; _iconLocation = value; return this; }

        public LinkBuilder WithEnvironmentTarget(string target)
        {
            var block = new byte[0x314];
            BinaryPrimitives.WriteUInt32LittleEndian(block, 0x314);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), 0xA0000001);
            Encoding.Latin1.GetBytes(target).CopyTo(block, 8);
            Encoding.Unicode.GetBytes(target).CopyTo(block, 268);
            _extra.Add(block);
            return this;
        }

        public LinkBuilder WithDarwinBlock()
        {
            var block = new byte[0x314];
            BinaryPrimitives.WriteUInt32LittleEndian(block, 0x314);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), 0xA0000006);
            _extra.Add(block);
            return this;
        }

        public LinkBuilder WithExtraBlock(uint signature, int size)
        {
            var block = new byte[size];
            BinaryPrimitives.WriteUInt32LittleEndian(block, (uint)size);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), signature);
            _extra.Add(block);
            return this;
        }

        public byte[] Build()
        {
            var bytes = new List<byte>();

            var header = new byte[0x4C];
            BinaryPrimitives.WriteUInt32LittleEndian(header, 0x4C);
            new byte[] { 0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 }
                .CopyTo(header, 4);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), _flags);
            bytes.AddRange(header);

            if (_idList is not null)
            {
                var size = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(size, (ushort)_idList.Length);
                bytes.AddRange(size);
                bytes.AddRange(_idList);
            }

            if (_linkInfo is not null)
            {
                bytes.AddRange(_linkInfo);
            }

            foreach (var value in new[] { _description, _relativePath, _workingDirectory, _arguments, _iconLocation })
            {
                if (value is null)
                {
                    continue;
                }

                var count = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(count, (ushort)value.Length);
                bytes.AddRange(count);
                bytes.AddRange(unicode ? Encoding.Unicode.GetBytes(value) : Encoding.Latin1.GetBytes(value));
            }

            foreach (var block in _extra)
            {
                bytes.AddRange(block);
            }

            bytes.AddRange(new byte[4]); // terminal block

            return [.. bytes];
        }
    }
}
