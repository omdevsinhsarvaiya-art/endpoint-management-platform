using System.Text.RegularExpressions;

namespace EndpointPlatform.Architecture.Tests;

/// <summary>
/// Keeps BitLocker protector types physically separated in source.
/// </summary>
/// <remarks>
/// <para>
/// Automatic recovery-key escrow derives every target it will ever act on from one
/// field: <c>DeviceBitLockerVolume.RecoveryProtectorIds</c>, populated by a WMI query
/// filtered to protector type 3 (<c>NumericalPassword</c>). For each id in that list
/// the agent is permitted to call <c>GetKeyProtectorNumericalPassword</c> -- the one
/// method in this platform that returns a 48-digit recovery key.
/// </para>
/// <para>
/// Adding TPM (type 1) and TPM+PIN (type 4) observation creates a way to get that
/// catastrophically wrong. The convenient implementation is to widen the existing
/// query -- drop the type filter, enumerate everything, sort it out later. That would
/// put startup-protector ids into the recovery list, and the escrow runner would then
/// ask Windows for the recovery password of a protector that has none.
/// </para>
/// <para>
/// These tests make that mistake fail the build rather than fail in production. They
/// are source scans on purpose: the property being defended is that the three queries
/// are separate <em>as written</em>, which no runtime assertion can observe.
/// </para>
/// </remarks>
public sealed class BitLockerProtectorSeparationTests
{
    private const string CollectorFile = "WindowsSecurityPostureCollector.cs";

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "agent")))
        {
            directory = directory.Parent;
        }

        Directory.Exists(Path.Combine(directory?.FullName ?? ".", "agent"))
            .ShouldBeTrue("the repository root should be findable from the test output directory");

        return directory!.FullName;
    }

    private static string CollectorSource() =>
        File.ReadAllText(Path.Combine(
            RepositoryRoot(), "agent", "EndpointAgent.Windows", CollectorFile));

    /// <summary>Source with comments and XML documentation removed.</summary>
    /// <remarks>
    /// Every claim below is about code. The remarks in that file discuss protector
    /// types and the method that must never be called by name, so scanning raw text
    /// would match prose and prove nothing.
    /// </remarks>
    private static string CollectorCode() => StripComments(CollectorSource());

    /// <summary>Removes block comments, XML documentation and line comments.</summary>
    /// <remarks>
    /// Load-bearing rather than tidiness. Several files in this area discuss the
    /// recovery-password API by name in order to say that they deliberately do not
    /// call it; a scan over raw text would read those disclaimers as violations and
    /// report the opposite of the truth.
    /// </remarks>
    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        source = Regex.Replace(source, @"^[ \t]*///.*$", string.Empty, RegexOptions.Multiline);
        source = Regex.Replace(source, @"//.*$", string.Empty, RegexOptions.Multiline);

        return source;
    }

    // ---- the recovery-password query is still filtered ---------------------

    /// <summary>
    /// The load-bearing assertion. The recovery-password read must still constrain
    /// itself to type 3.
    /// </summary>
    [Fact]
    public void The_recovery_password_query_still_filters_on_protector_type_three()
    {
        var code = CollectorCode();

        code.Contains("const int NumericalPasswordProtector = 3;", StringComparison.Ordinal)
            .ShouldBeTrue("the recovery-password query must keep its own literal type constant");

        // The filter is applied, not merely declared.
        code.Contains("parameters[\"KeyProtectorType\"] = (uint)NumericalPasswordProtector;", StringComparison.Ordinal)
            .ShouldBeTrue("the recovery-password query must pass its type as a typed WMI parameter");
    }

    /// <summary>
    /// Every GetKeyProtectors call sets a type. An unfiltered call returns protectors
    /// of all types, which is precisely the widening this feature must not introduce.
    /// </summary>
    [Fact]
    public void Every_protector_query_sets_a_key_protector_type()
    {
        var code = CollectorCode();

        var invocations = Regex.Matches(code, @"InvokeMethod\(\s*""GetKeyProtectors""").Count;
        var filters = Regex.Matches(code, @"parameters\[""KeyProtectorType""\]\s*=").Count;

        invocations.ShouldBeGreaterThan(0, "the collector should still enumerate protectors");
        filters.ShouldBe(invocations,
            "every GetKeyProtectors invocation must be filtered to exactly one protector type");
    }

    /// <summary>
    /// Each protector type is a literal assigned to a named constant, never a
    /// parameter a caller chooses. A shared reader taking a caller-supplied type is
    /// how type 4 would eventually reach the recovery list.
    /// </summary>
    [Fact]
    public void The_three_protector_types_are_declared_as_separate_literal_constants()
    {
        var code = CollectorCode();

        foreach (var declaration in new[]
                 {
                     "const int TpmProtector = 1;",
                     "const int NumericalPasswordProtector = 3;",
                     "const int TpmAndPinProtector = 4;",
                 })
        {
            code.Contains(declaration, StringComparison.Ordinal)
                .ShouldBeTrue($"expected a literal type constant: {declaration}");
        }
    }

    // ---- the escrow boundary ----------------------------------------------

    /// <summary>
    /// The J-4 boundary, restated for this feature: the collector reads protector
    /// identifiers and never key material, whatever protector types it now knows about.
    /// </summary>
    [Fact]
    public void The_collector_never_reads_a_recovery_password()
    {
        CollectorCode().Contains("GetKeyProtectorNumericalPassword", StringComparison.Ordinal)
            .ShouldBeFalse("the inventory collector reports protector identifiers only; "
                + "reading a recovery password is the escrow path and lives elsewhere");
    }

    /// <summary>
    /// The one method that does return a recovery password must remain reachable from
    /// exactly one file, so the set of places a protector id can become a key stays
    /// reviewable by hand.
    /// </summary>
    /// <remarks>
    /// Counts call sites, not mentions. Two other files name this method in their
    /// documentation precisely to record that they do not call it, and an earlier
    /// version of this test failed on those disclaimers.
    /// </remarks>
    [Fact]
    public void Only_one_agent_file_can_read_a_recovery_password()
    {
        var agentRoot = Path.Combine(RepositoryRoot(), "agent");

        var callers = Directory
            .EnumerateFiles(agentRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => StripComments(File.ReadAllText(f))
                .Contains("GetKeyProtectorNumericalPassword", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        callers.ShouldBe(["WindowsRecoveryPasswordReader.cs"],
            "exactly one reviewed file may reach the recovery-password API; found: "
            + string.Join(", ", callers));
    }

    /// <summary>
    /// The recovery-password reader is for recovery passwords. It must not have
    /// acquired any notion of a startup protector along the way, because a TPM+PIN id
    /// passed to it would be a type confusion with the worst possible blast radius.
    /// </summary>
    [Fact]
    public void The_recovery_password_reader_knows_nothing_about_startup_protectors()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "agent", "EndpointAgent.Windows", "WindowsRecoveryPasswordReader.cs"));

        var code = Regex.Replace(source, @"^[ \t]*///.*$", string.Empty, RegexOptions.Multiline);
        code = Regex.Replace(code, @"//.*$", string.Empty, RegexOptions.Multiline);

        foreach (var startupConcept in new[] { "TpmAndPin", "TpmProtector", "TpmPin" })
        {
            code.Contains(startupConcept, StringComparison.Ordinal)
                .ShouldBeFalse($"the recovery-password reader must not reference {startupConcept}");
        }
    }
}
