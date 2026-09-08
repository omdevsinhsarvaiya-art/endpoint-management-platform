using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Inventory;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Inventory;

/// <summary>
/// The composite's second step: asking each referenced executable what it says
/// about itself, once, bounded, and without letting one unreadable file cost
/// the rest.
/// </summary>
public sealed class SoftwareDiscoveryCollectorMetadataTests
{
    private sealed class FixedSource(params SoftwareEvidence[] evidence) : ISoftwareEvidenceSource
    {
        public string SourceName => "Fixed";

        public ValueTask<IReadOnlyList<SoftwareEvidence>> CollectEvidenceAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SoftwareEvidence>>(evidence);
    }

    private sealed class RecordingReader(Func<string, ExecutableMetadata?> read) : IExecutableMetadataReader
    {
        public List<string> Asked { get; } = [];

        public ExecutableMetadata? Read(string executablePath)
        {
            Asked.Add(executablePath);
            return read(executablePath);
        }
    }

    private static ExecutableMetadata Described(string product = "Contoso App") =>
        new(product, "1.2.3.4", "Contoso", "app.exe", "Contoso Application", "CN=Contoso Ltd", ExecutableSignatureStatus.Signed);

    private static SoftwareDiscoveryCollector Collector(IExecutableMetadataReader? reader, params SoftwareEvidence[] evidence) =>
        new([new FixedSource(evidence)], NullLogger<SoftwareDiscoveryCollector>.Instance, reader);

    [Fact]
    public async Task Each_referenced_executable_is_described_once()
    {
        var reader = new RecordingReader(_ => Described());

        var evidence = await Collector(reader,
            new SoftwareEvidence(EvidenceSource.AppPaths, ExecutablePath: @"C:\Tools\Contoso\app.exe"),
            new SoftwareEvidence(EvidenceSource.StartMenuShortcut, "Contoso", ExecutablePath: @"c:\tools\contoso\APP.EXE"),
            new SoftwareEvidence(EvidenceSource.StartMenuShortcut, "Contoso", ExecutablePath: "\"C:\\Tools\\Contoso\\app.exe\""),
            new SoftwareEvidence(EvidenceSource.AppPaths, ExecutablePath: @"C:\Tools\Other\other.exe")
        ).CollectEvidenceAsync();

        reader.Asked.ShouldBe([@"C:\Tools\Contoso\app.exe", @"C:\Tools\Other\other.exe"]);
        evidence.Count(e => e.Source == EvidenceSource.ExecutableMetadata).ShouldBe(2);
    }

    [Fact]
    public async Task Described_evidence_carries_what_the_file_said()
    {
        var evidence = await Collector(new RecordingReader(_ => Described()),
            new SoftwareEvidence(EvidenceSource.AppPaths, ExecutablePath: @"C:\Tools\Contoso\app.exe")
        ).CollectEvidenceAsync();

        var described = evidence.Single(e => e.Source == EvidenceSource.ExecutableMetadata);
        described.Name.ShouldBe("Contoso App");
        described.Version.ShouldBe("1.2.3.4");
        described.Publisher.ShouldBe("Contoso");
        described.FileDescription.ShouldBe("Contoso Application");
        described.SignerSubject.ShouldBe("CN=Contoso Ltd");
        described.SignatureStatus.ShouldBe("Signed");
        described.ExecutablePath.ShouldBe(@"C:\Tools\Contoso\app.exe");
        described.IsAuthoritative.ShouldBeFalse();
    }

    [Fact]
    public async Task Installation_records_are_not_files_and_are_not_described()
    {
        var reader = new RecordingReader(_ => Described());

        await Collector(reader,
            new SoftwareEvidence(EvidenceSource.UninstallRegistry, "Contoso", InstallLocation: @"C:\Tools\Contoso")
        ).CollectEvidenceAsync();

        reader.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_path_discovery_would_refuse_is_never_asked_about()
    {
        var reader = new RecordingReader(_ => Described());

        await Collector(reader,
            new SoftwareEvidence(EvidenceSource.AppPaths, ExecutablePath: @"\\server\share\app.exe"),
            new SoftwareEvidence(EvidenceSource.AppPaths, ExecutablePath: @"C:\Tools\..\app.exe"),
            new SoftwareEvidence(EvidenceSource.AppPaths, ExecutablePath: "app.exe")
        ).CollectEvidenceAsync();

        reader.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_reader_that_throws_costs_only_that_file()
    {
        var reader = new RecordingReader(path => path.Contains("bad", StringComparison.Ordinal)
            ? throw new IOException("locked")
            : Described());

        var evidence = await Collector(reader,
            new SoftwareEvidence(EvidenceSource.AppPaths, ExecutablePath: @"C:\Tools\bad.exe"),
            new SoftwareEvidence(EvidenceSource.AppPaths, ExecutablePath: @"C:\Tools\good.exe")
        ).CollectEvidenceAsync();

        evidence.Where(e => e.Source == EvidenceSource.ExecutableMetadata)
            .ShouldHaveSingleItem().ExecutablePath.ShouldBe(@"C:\Tools\good.exe");
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_adds_nothing()
    {
        var evidence = await Collector(new RecordingReader(_ => null),
            new SoftwareEvidence(EvidenceSource.AppPaths, ExecutablePath: @"C:\Tools\gone.exe")
        ).CollectEvidenceAsync();

        evidence.ShouldHaveSingleItem().Source.ShouldBe(EvidenceSource.AppPaths);
    }

    [Fact]
    public async Task Without_a_reader_nothing_is_described()
    {
        var evidence = await Collector(null,
            new SoftwareEvidence(EvidenceSource.AppPaths, ExecutablePath: @"C:\Tools\app.exe")
        ).CollectEvidenceAsync();

        evidence.ShouldHaveSingleItem().Source.ShouldBe(EvidenceSource.AppPaths);
    }

    [Fact]
    public async Task Description_is_bounded()
    {
        var reader = new RecordingReader(_ => Described());
        var many = Enumerable.Range(0, SoftwareDiscoveryCollector.MaxExecutables + 25)
            .Select(i => new SoftwareEvidence(EvidenceSource.AppPaths, ExecutablePath: $@"C:\Tools\app{i}.exe"))
            .ToArray();

        await Collector(reader, many).CollectEvidenceAsync();

        reader.Asked.Count.ShouldBe(SoftwareDiscoveryCollector.MaxExecutables);
    }

    [Fact]
    public async Task Cancellation_during_description_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        var reader = new RecordingReader(_ =>
        {
            cts.Cancel();
            return Described();
        });

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await Collector(reader,
                new SoftwareEvidence(EvidenceSource.AppPaths, ExecutablePath: @"C:\Tools\a.exe"),
                new SoftwareEvidence(EvidenceSource.AppPaths, ExecutablePath: @"C:\Tools\b.exe")
            ).CollectEvidenceAsync(cts.Token));
    }

    /// <summary>
    /// Through the whole pipeline: what the file said reaches the application, and
    /// a described reference is still not a row.
    /// </summary>
    [Fact]
    public async Task What_the_file_said_reaches_the_application_without_making_it_a_row()
    {
        var collector = Collector(new RecordingReader(_ => Described("Caffeine")),
            new SoftwareEvidence(EvidenceSource.StartMenuShortcut, "Caffeine", ExecutablePath: @"C:\Tools\caffeine64.exe"));

        var evidence = await collector.CollectEvidenceAsync();
        var app = ApplicationMerger.Merge(evidence).ShouldHaveSingleItem();

        app.Confidence.ShouldBe(DiscoveryConfidence.Referenced);
        app.SignerSubject.ShouldBe("CN=Contoso Ltd");
        app.Version.ShouldBe("1.2.3.4");
        SoftwareDiscoveryPipeline.Run(evidence).ShouldBeEmpty();
    }
}
