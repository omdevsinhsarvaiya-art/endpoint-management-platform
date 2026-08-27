using EndpointPlatform.Domain.Identity;

namespace EndpointPlatform.Domain.Tests.Identity;

/// <summary>
/// The temporary-administrator elevation lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// The property the whole feature rests on is that <b>an elevation never
/// outlives its authorized window</b>. Everything else here exists to stop that
/// property being reachable by another route: a late activation reopening a
/// closed window, a revoke that leaves the deadline behind, downtime banking
/// authorization, or a terminal record being edited back to life.
/// </para>
/// <para>
/// Time is passed in explicitly rather than read from the clock, matching the
/// convention <c>UsbAccessRequest</c> established: the entity takes <c>now</c>,
/// and callers supply it from an injected <see cref="TimeProvider"/>. That is
/// what lets expiry be tested at the boundary instead of approximately, and it
/// avoids the defect that shipped in the USB executor, which read the wall clock
/// and passed locally in the morning while failing in CI after the fixture's
/// timestamp had gone by.
/// </para>
/// </remarks>
public sealed class LocalAdminElevationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    private const string MachineSid = "S-1-5-21-1004336348-1177238915-682003330";
    private const string TargetSid = MachineSid + "-1001";
    private const string BuiltInSid = MachineSid + "-500";

    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Device = Guid.CreateVersion7();
    private static readonly Guid Actor = Guid.CreateVersion7();

    private static LocalAdminElevation Requested(string sid = TargetSid) =>
        LocalAdminElevation.Request(
            Org, Device, sid, "sarah", "Installing a signed vendor driver.", Actor, "admin@company.local", Now);

    private static LocalAdminElevation Approved(TimeSpan? duration = null)
    {
        var e = Requested();
        e.Approve(Actor, "admin@company.local", duration ?? TimeSpan.FromHours(1), Now);
        return e;
    }

    private static LocalAdminElevation Active()
    {
        var e = Approved();
        e.TryActivate(Now.AddMinutes(1)).ShouldBeTrue();
        return e;
    }

    // ---- creation ----------------------------------------------------------

    [Fact]
    public void A_new_request_confers_nothing()
    {
        var e = Requested();

        e.State.ShouldBe(LocalAdminElevationState.Requested);
        e.ExpiresAt.ShouldBeNull();
        e.IsLive(Now).ShouldBeFalse();
        e.IsTerminal.ShouldBeFalse();
    }

    /// <summary>
    /// The built-in Administrator can never be a target, at construction.
    /// </summary>
    /// <remarks>
    /// Refused in the constructor rather than at approval, so no path in the
    /// system can produce even a pending record proposing it. Matched by RID, so
    /// a renamed built-in is still refused.
    /// </remarks>
    [Fact]
    public void The_built_in_Administrator_cannot_be_elevated()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Requested(BuiltInSid));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_request_without_a_justification_is_refused(string? justification)
    {
        Should.Throw<ArgumentException>(() => LocalAdminElevation.Request(
            Org, Device, TargetSid, "sarah", justification!, Actor, "admin", Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_request_without_a_target_sid_is_refused(string? sid)
    {
        Should.Throw<ArgumentException>(() => LocalAdminElevation.Request(
            Org, Device, sid!, "sarah", "why", Actor, "admin", Now));
    }

    [Fact]
    public void A_request_without_a_device_or_actor_is_refused()
    {
        Should.Throw<ArgumentException>(() => LocalAdminElevation.Request(
            Org, Guid.Empty, TargetSid, "sarah", "why", Actor, "admin", Now));

        Should.Throw<ArgumentException>(() => LocalAdminElevation.Request(
            Org, Device, TargetSid, "sarah", "why", Guid.Empty, "admin", Now));
    }

    // ---- approval and duration --------------------------------------------

    [Fact]
    public void Approval_sets_an_absolute_deadline_from_the_moment_of_approval()
    {
        var e = Requested();
        e.Approve(Actor, "admin", TimeSpan.FromHours(2), Now);

        e.State.ShouldBe(LocalAdminElevationState.Approved);
        e.ExpiresAt.ShouldBe(Now.AddHours(2));
        e.IsLive(Now).ShouldBeTrue();
    }

    [Theory]
    [InlineData(1)]      // below the floor
    [InlineData(14)]
    [InlineData(481)]    // above the 8h ceiling
    [InlineData(1440)]
    [InlineData(0)]
    [InlineData(-60)]
    public void A_duration_outside_the_permitted_window_is_refused(int minutes)
    {
        var e = Requested();

        Should.Throw<ArgumentOutOfRangeException>(
            () => e.Approve(Actor, "admin", TimeSpan.FromMinutes(minutes), Now));

        e.State.ShouldBe(LocalAdminElevationState.Requested);
        e.ExpiresAt.ShouldBeNull();
    }

    [Fact]
    public void The_boundaries_themselves_are_permitted()
    {
        Requested().Approve(Actor, "admin", LocalAdminElevation.MinimumDuration, Now);
        Requested().Approve(Actor, "admin", LocalAdminElevation.MaximumDuration, Now);
    }

    /// <summary>
    /// The ceiling is shorter than the USB one, deliberately.
    /// </summary>
    /// <remarks>
    /// A read-only USB grant lets someone copy files off a stick. Administrator
    /// rights let them install software, stop this agent and edit the machine's
    /// security state. Pinned so the two are not "harmonised" later without a
    /// decision.
    /// </remarks>
    [Fact]
    public void The_ceiling_is_eight_hours()
    {
        LocalAdminElevation.MaximumDuration.ShouldBe(TimeSpan.FromHours(8));
        LocalAdminElevation.MinimumDuration.ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Self_approval_records_both_roles_without_inventing_a_deliberation()
    {
        var e = LocalAdminElevation.RequestAndApprove(
            Org, Device, TargetSid, "sarah", "Vendor driver.", Actor, "admin@company.local",
            TimeSpan.FromHours(1), Now);

        e.State.ShouldBe(LocalAdminElevationState.Approved);
        e.RequestedById.ShouldBe(Actor);
        e.ApprovedById.ShouldBe(Actor);
        e.RequestedAt.ShouldBe(Now);
        e.ApprovedAt.ShouldBe(Now);
    }

    // ---- activation --------------------------------------------------------

    [Fact]
    public void Activation_records_that_the_endpoint_confirmed_it()
    {
        var e = Approved();

        e.TryActivate(Now.AddMinutes(5)).ShouldBeTrue();

        e.State.ShouldBe(LocalAdminElevationState.Active);
        e.ActivatedAt.ShouldBe(Now.AddMinutes(5));
        e.IsLive(Now.AddMinutes(5)).ShouldBeTrue();
    }

    /// <summary>
    /// A task collected late cannot reopen a window that has already closed.
    /// </summary>
    /// <remarks>
    /// Entirely reachable: an endpoint offline when the elevation was approved
    /// collects the task hours later and reports back. Treating that report as an
    /// activation would resurrect a lapsed authorization, which is precisely the
    /// property this feature promises never to do.
    /// </remarks>
    [Fact]
    public void An_elevation_whose_deadline_has_passed_cannot_be_activated()
    {
        var e = Approved(TimeSpan.FromMinutes(30));

        e.TryActivate(Now.AddHours(2)).ShouldBeFalse();

        e.State.ShouldBe(LocalAdminElevationState.Approved);
        e.ActivatedAt.ShouldBeNull();
        e.IsLive(Now.AddHours(2)).ShouldBeFalse();
    }

    // ---- expiry ------------------------------------------------------------

    /// <summary>
    /// Liveness is decided by the clock, not by the stored state.
    /// </summary>
    /// <remarks>
    /// This is what makes a missed sweep a cosmetic problem rather than a
    /// security one: the record still says Active, and it still confers nothing.
    /// </remarks>
    [Fact]
    public void An_elevation_stops_conferring_rights_at_its_deadline_even_before_a_sweep()
    {
        var e = Active();
        var deadline = e.ExpiresAt!.Value;

        e.IsLive(deadline.AddSeconds(-1)).ShouldBeTrue();
        e.IsLive(deadline).ShouldBeFalse();
        e.IsLive(deadline.AddSeconds(1)).ShouldBeFalse();

        // Still labelled Active: nothing has swept it yet.
        e.State.ShouldBe(LocalAdminElevationState.Active);
    }

    [Fact]
    public void The_sweep_marks_a_lapsed_elevation_expired()
    {
        var e = Active();

        e.TryExpire(e.ExpiresAt!.Value.AddSeconds(1)).ShouldBeTrue();
        e.State.ShouldBe(LocalAdminElevationState.Expired);
        e.IsTerminal.ShouldBeTrue();
    }

    [Fact]
    public void The_sweep_leaves_an_elevation_that_has_not_lapsed_alone()
    {
        var e = Active();

        e.TryExpire(Now.AddMinutes(30)).ShouldBeFalse();
        e.State.ShouldBe(LocalAdminElevationState.Active);
    }

    /// <summary>
    /// Downtime does not bank authorization.
    /// </summary>
    /// <remarks>
    /// The deadline is absolute and starts at approval, so an endpoint that was
    /// offline for the whole window gets no elevated time at all rather than a
    /// window that begins when it reconnects.
    /// </remarks>
    [Fact]
    public void Time_spent_offline_does_not_extend_the_window()
    {
        var e = Approved(TimeSpan.FromHours(1));

        // The endpoint was unreachable for the entire window.
        e.IsLive(Now.AddHours(3)).ShouldBeFalse();
        e.TryActivate(Now.AddHours(3)).ShouldBeFalse();
        e.TryExpire(Now.AddHours(3)).ShouldBeTrue();
        e.State.ShouldBe(LocalAdminElevationState.Expired);
    }

    // ---- revoke ------------------------------------------------------------

    /// <summary>
    /// Revoking moves the deadline, so state-based and window-based questions
    /// cannot disagree.
    /// </summary>
    /// <remarks>
    /// Without this a revoked elevation would still look live to any query
    /// written against <c>ExpiresAt</c> — and a query is exactly how the policy
    /// sent to an endpoint is built.
    /// </remarks>
    [Fact]
    public void Revoking_ends_the_window_as_well_as_the_state()
    {
        var e = Active();

        e.TryRevoke(Actor, "admin", null, Now.AddMinutes(10)).ShouldBeTrue();

        e.State.ShouldBe(LocalAdminElevationState.Revoked);
        e.ExpiresAt.ShouldBe(Now.AddMinutes(10));
        e.IsLive(Now.AddMinutes(10)).ShouldBeFalse();
        e.RevokedAt.ShouldBe(Now.AddMinutes(10));
        e.DecisionNote.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void An_approved_but_never_activated_elevation_can_be_revoked()
    {
        var e = Approved();

        e.TryRevoke(Actor, "admin", "No longer needed.", Now.AddMinutes(2)).ShouldBeTrue();
        e.State.ShouldBe(LocalAdminElevationState.Revoked);
        e.DecisionNote.ShouldBe("No longer needed.");
    }

    [Fact]
    public void A_revoked_elevation_is_not_re_expired_by_the_sweeper()
    {
        var e = Active();
        e.TryRevoke(Actor, "admin", null, Now.AddMinutes(5));

        e.TryExpire(Now.AddHours(2)).ShouldBeFalse();
        e.State.ShouldBe(LocalAdminElevationState.Revoked);
    }

    // ---- failure -----------------------------------------------------------

    /// <summary>
    /// Failed means the account never became an administrator.
    /// </summary>
    [Fact]
    public void An_elevation_the_endpoint_could_not_apply_is_marked_failed()
    {
        var e = Approved();

        e.TryMarkFailed("Access denied adding the account to Administrators.", Now.AddMinutes(1))
            .ShouldBeTrue();

        e.State.ShouldBe(LocalAdminElevationState.Failed);
        e.FailureReason.ShouldNotBeNullOrWhiteSpace();
        e.IsLive(Now.AddMinutes(1)).ShouldBeFalse();
        e.IsTerminal.ShouldBeTrue();
    }

    /// <summary>
    /// A failure to REMOVE rights is not Failed, and must not be.
    /// </summary>
    /// <remarks>
    /// The decision that shapes this entity. When the deadline passes the
    /// authorization has ended, so the record expires whether or not the endpoint
    /// managed to de-elevate. Marking it Failed — or leaving it Active — would
    /// claim the account is still permitted to be an administrator, which is the
    /// opposite of the truth. That the account may still hold the rights is
    /// reported separately, by the endpoint, against DeviceLocalUser.
    /// </remarks>
    [Fact]
    public void An_active_elevation_cannot_be_marked_failed_it_expires_instead()
    {
        var e = Active();

        e.TryMarkFailed("Could not remove from Administrators.", Now.AddHours(2)).ShouldBeFalse();
        e.State.ShouldBe(LocalAdminElevationState.Active);

        // The authorization still ends on schedule.
        e.TryExpire(e.ExpiresAt!.Value.AddSeconds(1)).ShouldBeTrue();
        e.State.ShouldBe(LocalAdminElevationState.Expired);
    }

    // ---- illegal transitions ----------------------------------------------

    /// <summary>
    /// Every terminal state is immutable.
    /// </summary>
    /// <remarks>
    /// Asserted exhaustively rather than by sampling: a record that could be
    /// edited back to life is a record that could re-authorize an account nobody
    /// approved again.
    /// </remarks>
    [Fact]
    public void A_terminal_elevation_accepts_no_further_transition()
    {
        var terminals = new (string Name, LocalAdminElevation Elevation)[]
        {
            ("Rejected", RejectedElevation()),
            ("Expired", ExpiredElevation()),
            ("Revoked", RevokedElevation()),
            ("Failed", FailedElevation()),
        };

        foreach (var (name, e) in terminals)
        {
            var state = e.State;
            var later = Now.AddDays(1);

            e.IsTerminal.ShouldBeTrue(name);
            e.TryActivate(later).ShouldBeFalse(name);
            e.TryExpire(later).ShouldBeFalse(name);
            e.TryRevoke(Actor, "admin", null, later).ShouldBeFalse(name);
            e.TryMarkFailed("late", later).ShouldBeFalse(name);
            Should.Throw<InvalidOperationException>(
                () => e.Approve(Actor, "admin", TimeSpan.FromHours(1), later), name);
            Should.Throw<InvalidOperationException>(
                () => e.Reject(Actor, "admin", null, later), name);

            e.State.ShouldBe(state, name);
            e.IsLive(later).ShouldBeFalse(name);
        }
    }

    [Fact]
    public void An_approved_elevation_cannot_be_approved_or_rejected_again()
    {
        var e = Approved();

        Should.Throw<InvalidOperationException>(
            () => e.Approve(Actor, "admin", TimeSpan.FromHours(1), Now));
        Should.Throw<InvalidOperationException>(() => e.Reject(Actor, "admin", null, Now));

        e.State.ShouldBe(LocalAdminElevationState.Approved);
    }

    [Fact]
    public void An_active_elevation_cannot_be_re_approved()
    {
        var e = Active();

        Should.Throw<InvalidOperationException>(
            () => e.Approve(Actor, "admin", TimeSpan.FromHours(1), Now));
        e.TryActivate(Now.AddMinutes(2)).ShouldBeFalse();
    }

    [Fact]
    public void A_requested_elevation_cannot_be_activated_revoked_or_expired()
    {
        var e = Requested();

        e.TryActivate(Now).ShouldBeFalse();
        e.TryRevoke(Actor, "admin", null, Now).ShouldBeFalse();
        e.TryExpire(Now.AddDays(1)).ShouldBeFalse();
        e.TryMarkFailed("nope", Now).ShouldBeFalse();

        e.State.ShouldBe(LocalAdminElevationState.Requested);
    }

    [Fact]
    public void Rejection_is_recorded_with_its_decider()
    {
        var e = Requested();
        e.Reject(Actor, "admin@company.local", "Not justified.", Now.AddMinutes(3));

        e.State.ShouldBe(LocalAdminElevationState.Rejected);
        e.ApprovedById.ShouldBe(Actor);
        e.DecisionNote.ShouldBe("Not justified.");
        e.ExpiresAt.ShouldBeNull();
        e.IsLive(Now.AddMinutes(3)).ShouldBeFalse();
    }

    // ---- one live elevation per account ------------------------------------

    /// <summary>
    /// The conflict check, and an explicit note on what it does not guarantee.
    /// </summary>
    /// <remarks>
    /// This is a pure read over a snapshot, so two concurrent requests can both
    /// see no live elevation and both pass it. The real guarantee has to be a
    /// partial unique index in PostgreSQL over (DeviceId, TargetSid) restricted
    /// to the non-terminal states, added with persistence in M12-2. These tests
    /// pin the domain half and the comment records why it is only half.
    /// </remarks>
    [Fact]
    public void A_second_elevation_for_the_same_account_conflicts_while_one_is_live()
    {
        var existing = new[] { Active() };

        LocalAdminElevation.WouldConflict(existing, Device, TargetSid, Now.AddMinutes(1))
            .ShouldBeTrue();
    }

    [Fact]
    public void A_pending_request_also_conflicts()
    {
        var existing = new[] { Requested() };

        LocalAdminElevation.WouldConflict(existing, Device, TargetSid, Now).ShouldBeTrue();
    }

    [Fact]
    public void Once_the_first_elevation_ends_a_second_no_longer_conflicts()
    {
        var first = Active();
        first.TryRevoke(Actor, "admin", null, Now.AddMinutes(5));

        LocalAdminElevation.WouldConflict([first], Device, TargetSid, Now.AddMinutes(6))
            .ShouldBeFalse();
    }

    [Fact]
    public void An_elapsed_elevation_does_not_conflict_even_before_the_sweep_relabels_it()
    {
        var first = Active();

        LocalAdminElevation.WouldConflict(
            [first], Device, TargetSid, first.ExpiresAt!.Value.AddSeconds(1)).ShouldBeFalse();
    }

    [Fact]
    public void A_different_account_or_device_does_not_conflict()
    {
        var existing = new[] { Active() };

        LocalAdminElevation.WouldConflict(existing, Device, MachineSid + "-1002", Now).ShouldBeFalse();
        LocalAdminElevation.WouldConflict(existing, Guid.CreateVersion7(), TargetSid, Now).ShouldBeFalse();
    }

    /// <summary>SIDs are compared case-insensitively, as Windows treats them.</summary>
    [Fact]
    public void The_conflict_check_matches_sids_case_insensitively()
    {
        var existing = new[] { Active() };

        LocalAdminElevation.WouldConflict(existing, Device, TargetSid.ToLowerInvariant(), Now)
            .ShouldBeTrue();
    }

    // ---- fixtures for the terminal sweep -----------------------------------

    private static LocalAdminElevation RejectedElevation()
    {
        var e = Requested();
        e.Reject(Actor, "admin", null, Now);
        return e;
    }

    private static LocalAdminElevation ExpiredElevation()
    {
        var e = Active();
        e.TryExpire(e.ExpiresAt!.Value.AddSeconds(1));
        return e;
    }

    private static LocalAdminElevation RevokedElevation()
    {
        var e = Active();
        e.TryRevoke(Actor, "admin", null, Now.AddMinutes(5));
        return e;
    }

    private static LocalAdminElevation FailedElevation()
    {
        var e = Approved();
        e.TryMarkFailed("could not apply", Now.AddMinutes(1));
        return e;
    }
}
