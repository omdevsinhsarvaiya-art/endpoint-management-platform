using EndpointAgent.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Identity;

/// <summary>One account authorized to hold administrator rights, as the endpoint sees it.</summary>
public sealed record ElevationGrant(string Sid, DateTimeOffset ExpiresAt);

/// <param name="Elevated">Accounts raised to administrator by this reconcile.</param>
/// <param name="Lowered">Accounts returned to standard by this reconcile.</param>
/// <param name="Refused">
/// Accounts a safety rule refused to change, with the reason. Never hidden: a
/// refusal means the machine is not in the state the server believes it is.
/// </param>
public sealed record ElevationReconcileOutcome(
    IReadOnlyList<string> Elevated,
    IReadOnlyList<string> Lowered,
    IReadOnlyList<(string Sid, string Reason)> Refused)
{
    public bool Succeeded => Refused.Count == 0;
}

/// <summary>
/// Brings this machine's administrator membership into line with the elevations
/// the platform has authorized.
/// </summary>
/// <remarks>
/// <para>
/// One rule governs everything here: <b>an account holds elevated administrator
/// rights only while a live, in-date authorization names it.</b> Not "until the
/// server tells us to remove it" — the payload is whole state, so an account's
/// absence is the instruction, and an expired deadline ends the authorization
/// without any message arriving at all. That is what makes an elevation lapse on
/// time on a machine that has been offline for a week.
/// </para>
/// <para>
/// <b>This is the only writer of elevation-controlled membership.</b> Nothing
/// else adds or removes an account from Administrators on behalf of an elevation.
/// Two writers over one group is how drift begins: each would see the other's
/// change as unexplained and try to correct it.
/// </para>
/// <para>
/// Every decision that could remove administrator rights is taken against
/// <em>live</em> Windows state read moments earlier, never against the server's
/// inventory. Inventory is minutes old at best, and the question being asked —
/// "would this leave the machine with no usable administrator?" — cannot be
/// answered safely from a stale snapshot.
/// </para>
/// </remarks>
public sealed class LocalAdminElevationManager(
    ILocalAccountsControl accounts,
    IElevationLedger ledger,
    TimeProvider timeProvider,
    ILogger<LocalAdminElevationManager> logger)
{
    /// <summary>Well-known SID of BUILTIN\Administrators; identical on every Windows install.</summary>
    private const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>Well-known SID of BUILTIN\Users, the baseline every interactive account keeps.</summary>
    private const string UsersSid = "S-1-5-32-545";

    private readonly ILocalAccountsControl _accounts = accounts
        ?? throw new ArgumentNullException(nameof(accounts));

    private readonly IElevationLedger _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<LocalAdminElevationManager> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Serialises reconciles so two cannot fight over the same account.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// When the newest policy this agent has accepted was built.
    /// </summary>
    /// <remarks>
    /// Older policies are ignored. Without this, a task queued before a revocation
    /// but delivered after it — entirely possible for a machine that was offline
    /// for both — would reinstate the rights that were revoked.
    /// </remarks>
    private DateTimeOffset _acceptedIssuedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Applies a whole-state elevation set and reports what actually changed.
    /// </summary>
    public async Task<ElevationReconcileOutcome> ApplyAsync(
        IReadOnlyList<ElevationGrant> grants,
        DateTimeOffset issuedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grants);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (issuedAt < _acceptedIssuedAt)
            {
                _logger.LogInformation(
                    "Ignoring an elevation set issued {IssuedAt}; a newer one issued {Accepted} is already "
                    + "in force. A late message must not reinstate authorization that has since ended.",
                    issuedAt, _acceptedIssuedAt);

                return new ElevationReconcileOutcome([], [], []);
            }

            _acceptedIssuedAt = issuedAt;

            var now = _timeProvider.GetUtcNow();

            // Expiry is judged here, against this machine's clock. A grant whose
            // deadline has already passed is dropped rather than applied, so a
            // task collected late by an endpoint that was offline cannot open a
            // window that has already closed.
            var authorized = grants
                .Where(g => !string.IsNullOrWhiteSpace(g.Sid) && g.ExpiresAt > now)
                .Select(g => g.Sid)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var live = await _accounts.GetLiveAccountsAsync(cancellationToken);
            var controlled = new HashSet<string>(
                await _ledger.LoadAsync(cancellationToken), StringComparer.OrdinalIgnoreCase);

            var elevated = new List<string>();
            var lowered = new List<string>();
            var refused = new List<(string, string)>();

            // ---- raise ----------------------------------------------------
            foreach (var sid in authorized)
            {
                var account = Find(live, sid);

                if (account is null)
                {
                    refused.Add((sid, "No local account with that SID exists on this machine."));
                    continue;
                }

                if (IsBuiltInAdministrator(sid))
                {
                    // Unreachable through the platform, which refuses it at three
                    // earlier layers. Refused again here because this is the last
                    // place before Windows changes, and a payload is input.
                    refused.Add((sid, "The built-in Administrator account is protected."));
                    continue;
                }

                if (account.IsAdministrator)
                {
                    // Already an administrator. If we did not raise it, it is not
                    // ours to manage and must not join the ledger -- otherwise a
                    // pre-existing administrator would be lowered when the
                    // elevation ended.
                    continue;
                }

                await _accounts.SetGroupMembershipAsync(
                    AdministratorsSid, sid, isMember: true, cancellationToken);

                controlled.Add(sid);
                elevated.Add(sid);
            }

            // ---- lower ----------------------------------------------------
            // Only accounts this agent raised. An administrator we never elevated
            // is somebody else's decision and is left exactly as it is.
            foreach (var sid in controlled.Except(authorized, StringComparer.OrdinalIgnoreCase).ToList())
            {
                var account = Find(live, sid);

                if (account is null)
                {
                    // The account is gone. Nothing to lower, and keeping it on the
                    // ledger would leave a permanent entry for a deleted user.
                    controlled.Remove(sid);
                    continue;
                }

                if (IsBuiltInAdministrator(sid))
                {
                    refused.Add((sid, "The built-in Administrator account is protected."));
                    continue;
                }

                if (!account.IsAdministrator)
                {
                    // Already lowered, by us on a previous run or by an
                    // administrator by hand. Either way there is nothing to do.
                    controlled.Remove(sid);
                    continue;
                }

                // The last-administrator guard, evaluated against state read from
                // Windows moments ago. Refusing here leaves an account elevated
                // past its window, which is reported rather than hidden -- the
                // alternative is a machine nobody can administer.
                if (WouldStrandTheMachine(live, sid))
                {
                    refused.Add((sid,
                        "Refused: this is the last enabled administrator on the machine. Lowering it "
                        + "would leave nobody able to administer it."));
                    continue;
                }

                // Establish the standard-user baseline first, so an account whose
                // only membership was Administrators is never briefly in no group
                // at all.
                await _accounts.SetGroupMembershipAsync(UsersSid, sid, isMember: true, cancellationToken);
                await _accounts.SetGroupMembershipAsync(
                    AdministratorsSid, sid, isMember: false, cancellationToken);

                controlled.Remove(sid);
                lowered.Add(sid);
            }

            // ---- verify ---------------------------------------------------
            // A Windows call that did not throw is not evidence the membership
            // changed. Everything upstream -- the task result, the console badge,
            // the audit record -- is derived from this re-read rather than from
            // the assumption that the call worked.
            var observed = await _accounts.GetLiveAccountsAsync(cancellationToken);

            foreach (var sid in authorized)
            {
                if (Find(observed, sid) is { } account && !account.IsAdministrator
                    && !refused.Any(r => Same(r.Item1, sid)))
                {
                    refused.Add((sid, "Verification failed: the account is still not an administrator."));
                }
            }

            foreach (var sid in lowered)
            {
                if (Find(observed, sid) is { IsAdministrator: true })
                {
                    refused.Add((sid, "Verification failed: the account is still an administrator."));
                }
            }

            await SaveLedgerAsync(controlled, cancellationToken);

            _logger.LogInformation(
                "Elevation reconcile: {Elevated} raised, {Lowered} lowered, {Refused} refused.",
                elevated.Count, lowered.Count, refused.Count);

            return new ElevationReconcileOutcome(elevated, lowered, refused);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// True when lowering this account would leave no enabled administrator.
    /// </summary>
    /// <remarks>
    /// Mirrors the server's <c>LocalAccountSafetyRules</c> last-admin rule, but
    /// evaluated against live Windows state rather than reported inventory. Both
    /// layers enforce it: the server refuses obviously-unsafe requests early, and
    /// this refuses them at the moment they would take effect, when the answer
    /// cannot have gone stale.
    /// </remarks>
    private static bool WouldStrandTheMachine(IReadOnlyList<LiveLocalAccount> live, string sid) =>
        !live.Any(a => a.IsAdministrator && a.Enabled && !Same(a.Sid, sid));

    /// <summary>The built-in Administrator always has RID 500.</summary>
    private static bool IsBuiltInAdministrator(string sid) =>
        sid.EndsWith("-500", StringComparison.OrdinalIgnoreCase);

    private static LiveLocalAccount? Find(IReadOnlyList<LiveLocalAccount> accounts, string sid) =>
        accounts.FirstOrDefault(a => Same(a.Sid, sid));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private async Task SaveLedgerAsync(HashSet<string> controlled, CancellationToken cancellationToken)
    {
        try
        {
            await _ledger.SaveAsync(controlled.ToList(), cancellationToken);
        }
        catch (Exception ex)
        {
            // Loud, because the consequence is an account whose elevation this
            // agent will no longer know to withdraw.
            _logger.LogError(
                ex, "Could not persist the elevation ledger. An elevated account may need to be "
                + "lowered by hand if this agent restarts before the next successful save.");
        }
    }
}
