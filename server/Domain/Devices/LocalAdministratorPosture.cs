namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// Whether an endpoint's interactive accounts follow standard-user-by-default.
/// </summary>
public enum LocalAdminCompliance
{
    /// <summary>
    /// Nothing has been reported yet, so no verdict can be given.
    /// </summary>
    /// <remarks>
    /// Deliberately distinct from <see cref="Compliant"/>. A machine that has
    /// never sent an account inventory — newly enrolled, or offline since before
    /// this feature shipped — is not evidence of good posture, and rendering it
    /// as compliant would quietly overstate how much of the estate has been
    /// checked.
    /// </remarks>
    Unknown = 0,

    /// <summary>No interactive account on this endpoint holds local administrator rights.</summary>
    Compliant = 1,

    /// <summary>At least one interactive account holds local administrator rights.</summary>
    NonCompliant = 2,
}

/// <summary>
/// One account, and why it does or does not count towards the verdict.
/// </summary>
/// <param name="ExcludedReason">
/// Null when the account counts. Otherwise the reason it was set aside, which is
/// shown rather than hidden — an operator looking at a "compliant" machine that
/// visibly has an Administrator account needs to see why it was discounted, or
/// they will not believe the verdict.
/// </param>
public sealed record LocalAdminFinding(
    string Sid,
    string Username,
    bool Enabled,
    bool IsAdministrator,
    string? ExcludedReason)
{
    /// <summary>True when this account is what makes the endpoint non-compliant.</summary>
    public bool CountsAgainstCompliance => IsAdministrator && ExcludedReason is null;
}

/// <param name="InteractiveAdministrators">
/// The accounts that make the endpoint non-compliant. Empty when compliant.
/// </param>
/// <param name="Findings">Every account considered, including the excluded ones.</param>
public sealed record LocalAdminPostureResult(
    LocalAdminCompliance Compliance,
    IReadOnlyList<LocalAdminFinding> InteractiveAdministrators,
    IReadOnlyList<LocalAdminFinding> Findings);

/// <summary>
/// Decides whether an endpoint's interactive users are standard users.
/// </summary>
/// <remarks>
/// <para>
/// Pure predicates over a reported snapshot, in the same shape as
/// <see cref="LocalAccountSafetyRules"/> and for the same reason: the rule needs
/// to be assertable without a database, a device, or a running agent.
/// </para>
/// <para>
/// <b>This evaluates. It never changes anything.</b> Nothing in Milestone 11b
/// removes an account from Administrators except the expiry of an elevation this
/// platform itself granted. An endpoint that was set up with an administrative
/// user keeps it until somebody decides otherwise; the platform's job here is to
/// make that visible rather than to quietly correct it. A silent downgrade would
/// be indistinguishable, from the user's side, from the machine breaking.
/// </para>
/// <para>
/// <b>Scope, stated because the gap matters.</b> Membership is taken from what
/// the agent reports for the local Administrators group, which is direct
/// membership. An account that holds administrator rights only through a nested
/// group is not detected here and is not claimed to be. That is a real
/// limitation on domain-joined machines and is documented rather than papered
/// over; on the standalone endpoints this platform manages, direct membership is
/// how local administrators are actually granted.
/// </para>
/// </remarks>
public static class LocalAdministratorPosture
{
    /// <summary>
    /// Well-known RIDs for accounts Windows creates itself.
    /// </summary>
    /// <remarks>
    /// Matched on the RID rather than the name throughout. Every one of these can
    /// be renamed — renaming the built-in Administrator is a standard hardening
    /// step — and the names are localized besides, so a name-based rule would
    /// silently stop working on a German install or after a rename.
    /// </remarks>
    private static readonly int[] BuiltInRids =
    [
        500, // Administrator
        501, // Guest
        503, // DefaultAccount
        504, // WDAGUtilityAccount
    ];

    /// <summary>
    /// Evaluates a device's reported local accounts.
    /// </summary>
    /// <param name="accounts">
    /// The device's last reported local accounts. An empty or null collection
    /// yields <see cref="LocalAdminCompliance.Unknown"/> — see the enum.
    /// </param>
    public static LocalAdminPostureResult Evaluate(IReadOnlyCollection<LocalAccountView>? accounts)
    {
        if (accounts is null || accounts.Count == 0)
        {
            return new LocalAdminPostureResult(LocalAdminCompliance.Unknown, [], []);
        }

        var findings = accounts
            .Select(a => new LocalAdminFinding(
                a.Sid, a.Username, a.Enabled, a.IsAdministrator, ExclusionReasonFor(a)))
            .ToList();

        var offenders = findings.Where(f => f.CountsAgainstCompliance).ToList();

        return new LocalAdminPostureResult(
            offenders.Count == 0 ? LocalAdminCompliance.Compliant : LocalAdminCompliance.NonCompliant,
            offenders,
            findings);
    }

    /// <summary>
    /// Why an account is set aside, or null when it counts.
    /// </summary>
    /// <remarks>
    /// Two exclusions, both about the same question: can a person actually log in
    /// with this account and act as an administrator?
    /// <list type="bullet">
    ///   <item><b>Disabled</b> — nobody can sign in with it, so it confers nothing
    ///   interactively. It is still reported, because an administrator account
    ///   that is merely disabled is one setting away from being live again.</item>
    ///   <item><b>Built-in</b> — Windows creates these and this platform refuses
    ///   to delete or disable RID 500 precisely so an organization cannot lock
    ///   itself out. Counting an account we protect by policy would make every
    ///   endpoint permanently non-compliant with no available remedy, which is a
    ///   verdict nobody could act on.</item>
    /// </list>
    /// </remarks>
    private static string? ExclusionReasonFor(LocalAccountView account)
    {
        if (!account.Enabled)
        {
            return "Disabled — cannot be used to sign in.";
        }

        if (IsBuiltIn(account.Sid))
        {
            return "Built-in Windows account, protected by this platform and not interactively used.";
        }

        return null;
    }

    /// <summary>True for an account Windows created itself, matched by RID.</summary>
    public static bool IsBuiltIn(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid))
        {
            return false;
        }

        var lastDash = sid.LastIndexOf('-');
        if (lastDash < 0 || lastDash == sid.Length - 1)
        {
            return false;
        }

        return int.TryParse(sid[(lastDash + 1)..], out var rid) && BuiltInRids.Contains(rid);
    }
}
