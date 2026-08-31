using EndpointAgent.Core.BitLocker;
using EndpointPlatform.Domain.BitLocker;

namespace EndpointPlatform.Domain.Tests.BitLocker;

/// <summary>
/// The agent and the server must agree on what a recovery password is.
/// </summary>
/// <remarks>
/// <para>
/// The rule is stated twice -- <see cref="BitLockerRecoveryPassword"/> on the server
/// and <see cref="RecoveryPasswordFormat"/> on the endpoint -- because the agent
/// cannot reference the server's domain assembly. Duplication is the price of that
/// boundary; silent divergence is not, and this test is what makes the duplication
/// safe.
/// </para>
/// <para>
/// Divergence would fail in one of two directions, and the second is much worse. If
/// the agent were stricter, valid keys would go unescrowed and somebody would
/// eventually notice a machine with no filed key. If the agent were <em>looser</em>,
/// it would seal something the server would have rejected -- and because the server
/// never opens an automatic envelope during ingestion, nothing downstream would
/// catch it. The bad value would sit in the table looking exactly like a good one
/// until the day a disk needed unlocking.
/// </para>
/// <para>
/// So the two are driven over the same corpus and required to agree on every case,
/// rather than each being checked against its own expectations.
/// </para>
/// </remarks>
public sealed class RecoveryPasswordConformanceTests
{
    /// <summary>
    /// Cases spanning both sides of every rule: shape, digits, group count, and the
    /// divide-by-eleven checksum at and around its boundaries.
    /// </summary>
    private static readonly string?[] Candidates =
    [
        null,
        "",
        "   ",
        // Valid.
        "011000-011000-011000-011000-011000-011000-011000-011000",
        "  011000-011000-011000-011000-011000-011000-011000-011000  ",
        "000000-000000-000000-000000-000000-000000-000000-000000",
        // Group count.
        "011000",
        "011000-011000-011000-011000-011000-011000-011000",
        "011000-011000-011000-011000-011000-011000-011000-011000-011000",
        // Shape.
        "01100a-011000-011000-011000-011000-011000-011000-011000",
        "11000-011000-011000-011000-011000-011000-011000-011000",
        "0110000-011000-011000-011000-011000-011000-011000-011000",
        "000000000000000000000000000000000000000000000000",
        "011000 011000 011000 011000 011000 011000 011000 011000",
        // Checksum: not a multiple of eleven.
        "011001-011000-011000-011000-011000-011000-011000-011000",
        "000001-000000-000000-000000-000000-000000-000000-000000",
        // Checksum boundary: 720720 / 11 = 65520, inside 16 bits.
        "720720-011000-011000-011000-011000-011000-011000-011000",
        // 725725 / 11 = 65975, outside 16 bits.
        "725725-011000-011000-011000-011000-011000-011000-011000",
        // Digits that are not ASCII.
        "٠١١٠٠٠-011000-011000-011000-011000-011000-011000-011000",
    ];

    public static TheoryData<string?> Corpus()
    {
        var data = new TheoryData<string?>();

        foreach (var candidate in Candidates)
        {
            data.Add(candidate);
        }

        return data;
    }

    /// <summary>
    /// Agreement on the verdict. The categories are named differently on each side
    /// and are not compared -- only whether the value is accepted, which is what
    /// actually decides whether a key gets sealed.
    /// </summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void The_agent_and_the_server_agree(string? candidate)
    {
        var server = BitLockerRecoveryPassword.Validate(candidate) == RecoveryPasswordError.None;
        var agent = RecoveryPasswordFormat.IsWellFormed(candidate);

        agent.ShouldBe(
            server,
            $"the agent and server validators disagree about a candidate. The agent seals without "
            + $"the server ever opening the envelope, so a looser agent rule would file a key "
            + $"nothing can catch. (server accepted: {server}, agent accepted: {agent})");
    }

    /// <summary>
    /// The constants the two rules are built from, compared directly. A change to
    /// one side's shape is caught here even if the corpus above happens not to
    /// contain a case that distinguishes them.
    /// </summary>
    [Fact]
    public void Both_sides_describe_the_same_shape()
    {
        RecoveryPasswordFormat.GroupCount.ShouldBe(BitLockerRecoveryPassword.GroupCount);
        RecoveryPasswordFormat.GroupLength.ShouldBe(BitLockerRecoveryPassword.GroupLength);
    }

    /// <summary>
    /// Guards the corpus itself. A set of cases that every implementation accepts,
    /// or rejects, would let the agreement test pass while proving nothing.
    /// </summary>
    [Fact]
    public void The_corpus_contains_both_accepted_and_rejected_cases()
    {
        var verdicts = Candidates.Select(RecoveryPasswordFormat.IsWellFormed).ToList();

        verdicts.ShouldContain(true);
        verdicts.ShouldContain(false);
    }
}
