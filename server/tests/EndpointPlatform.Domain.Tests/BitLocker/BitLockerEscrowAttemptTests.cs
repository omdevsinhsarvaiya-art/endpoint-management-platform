using EndpointPlatform.Domain.BitLocker;

namespace EndpointPlatform.Domain.Tests.BitLocker;

/// <summary>
/// The retry schedule for automatic escrow.
/// </summary>
/// <remarks>
/// Two failure modes are being guarded against, and they pull in opposite
/// directions. Giving up too readily leaves a machine with no filed key and nobody
/// looking at it. Never giving up means an endpoint whose Windows refuses the call
/// asks again forever, hammering both the machine and the API. The schedule is the
/// compromise, and it terminates.
/// </remarks>
public sealed class BitLockerEscrowAttemptTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private const string Volume = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";
    private const string Protector = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

    private static BitLockerEscrowAttempt New() =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), Volume, Protector, Now);

    [Fact]
    public void A_new_protector_is_pending_and_immediately_due()
    {
        var attempt = New();

        attempt.State.ShouldBe(BitLockerEscrowAttemptState.Pending);
        attempt.AttemptCount.ShouldBe(0);
        attempt.IsDue(Now).ShouldBeTrue();
    }

    /// <summary>The approved schedule, asserted exactly rather than approximately.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 60)]
    public void Each_failure_schedules_the_next_attempt_further_out(int failures, int expectedMinutes)
    {
        var attempt = New();

        for (var i = 0; i < failures; i++)
        {
            attempt.RecordFailure(BitLockerEscrowFailureCategory.WindowsRefused, Now);
        }

        attempt.State.ShouldBe(BitLockerEscrowAttemptState.Failed);
        attempt.NextAttemptAt.ShouldBe(Now.AddMinutes(expectedMinutes));
    }

    [Fact]
    public void The_fifth_failure_exhausts_the_schedule_and_stops()
    {
        var attempt = New();

        for (var i = 0; i < BitLockerEscrowAttempt.MaxAttempts; i++)
        {
            attempt.RecordFailure(BitLockerEscrowFailureCategory.WindowsRefused, Now);
        }

        attempt.State.ShouldBe(BitLockerEscrowAttemptState.RetryExhausted);
        attempt.AttemptCount.ShouldBe(5);

        // The assertion that actually prevents hammering: nothing is due, ever,
        // without an administrator stepping in.
        attempt.NextAttemptAt.ShouldBeNull();
        attempt.IsDue(Now.AddYears(1)).ShouldBeFalse();
    }

    [Fact]
    public void An_attempt_is_not_due_before_its_scheduled_time()
    {
        var attempt = New();
        attempt.RecordFailure(BitLockerEscrowFailureCategory.WindowsRefused, Now);

        attempt.IsDue(Now.AddSeconds(59)).ShouldBeFalse();
        attempt.IsDue(Now.AddMinutes(1)).ShouldBeTrue();
    }

    [Fact]
    public void Success_is_terminal_and_schedules_nothing()
    {
        var attempt = New();
        attempt.RecordFailure(BitLockerEscrowFailureCategory.WindowsRefused, Now);
        attempt.RecordSuccess(Now.AddMinutes(1));

        attempt.State.ShouldBe(BitLockerEscrowAttemptState.Escrowed);
        attempt.NextAttemptAt.ShouldBeNull();
        attempt.LastFailure.ShouldBe(BitLockerEscrowFailureCategory.None);
        attempt.IsDue(Now.AddDays(1)).ShouldBeFalse();
    }

    /// <summary>
    /// The escape hatch for an exhausted protector, and the only thing that
    /// re-arms one.
    /// </summary>
    [Fact]
    public void An_administrator_reset_re_arms_an_exhausted_protector()
    {
        var attempt = New();
        var admin = Guid.CreateVersion7();

        for (var i = 0; i < BitLockerEscrowAttempt.MaxAttempts; i++)
        {
            attempt.RecordFailure(BitLockerEscrowFailureCategory.WindowsRefused, Now);
        }

        attempt.Reset(admin, Now.AddHours(2));

        attempt.State.ShouldBe(BitLockerEscrowAttemptState.Pending);
        attempt.AttemptCount.ShouldBe(0);
        attempt.LastFailure.ShouldBe(BitLockerEscrowFailureCategory.None);
        attempt.ResetByUserId.ShouldBe(admin);
        attempt.IsDue(Now.AddHours(2)).ShouldBeTrue();
    }

    /// <summary>
    /// A reset restores the full budget rather than a single extra attempt: the
    /// administrator is expected to have fixed something.
    /// </summary>
    [Fact]
    public void A_reset_restores_the_whole_schedule()
    {
        var attempt = New();

        for (var i = 0; i < BitLockerEscrowAttempt.MaxAttempts; i++)
        {
            attempt.RecordFailure(BitLockerEscrowFailureCategory.WindowsRefused, Now);
        }

        attempt.Reset(Guid.CreateVersion7(), Now);
        attempt.RecordFailure(BitLockerEscrowFailureCategory.WindowsRefused, Now);

        attempt.State.ShouldBe(BitLockerEscrowAttemptState.Failed);
        attempt.NextAttemptAt.ShouldBe(Now.AddMinutes(1));
    }

    /// <summary>
    /// Failure categories are a closed set precisely so no message -- and so
    /// nothing derived from a value -- can reach the audit trail through them.
    /// </summary>
    [Fact]
    public void The_last_failure_is_recorded_as_a_category()
    {
        var attempt = New();
        attempt.RecordFailure(BitLockerEscrowFailureCategory.FingerprintMismatch, Now);

        attempt.LastFailure.ShouldBe(BitLockerEscrowFailureCategory.FingerprintMismatch);
    }
}
