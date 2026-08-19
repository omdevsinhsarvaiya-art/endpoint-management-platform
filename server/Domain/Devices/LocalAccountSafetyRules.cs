namespace EndpointPlatform.Domain.Devices;

/// <summary>A minimal projection of a local account for the safety rules.</summary>
public sealed record LocalAccountView(string Sid, string Username, bool Enabled, bool IsAdministrator);

/// <summary>
/// Domain safety rules that stop a local-account mutation from locking the
/// organization out of a managed endpoint.
/// </summary>
/// <remarks>
/// <para>
/// These are pure, side-effect-free predicates over a snapshot of the device's
/// local accounts. The Admin API runs them as a fast pre-check against the last
/// reported inventory; the agent re-checks the same invariants against LIVE
/// Windows state at execution time, because inventory can be stale. Both layers
/// enforce them — the UI is never the boundary.
/// </para>
/// <para>
/// Two protections:
/// <list type="bullet">
///   <item><b>Protected account</b>: the built-in Administrator (RID 500) cannot be
///   deleted or disabled.</item>
///   <item><b>Last administrator</b>: deleting, disabling, or demoting the last
///   <em>enabled</em> member of the local Administrators group is refused — that is
///   the classic self-lockout.</item>
/// </list>
/// </para>
/// </remarks>
public static class LocalAccountSafetyRules
{
    /// <summary>The built-in Administrator account always has RID 500 (its SID ends "-500").</summary>
    public static bool IsBuiltInAdministrator(string sid) =>
        sid.EndsWith("-500", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns a refusal reason, or null when the delete is permitted.</summary>
    public static string? ValidateDelete(string targetSid, IReadOnlyCollection<LocalAccountView> users)
    {
        if (IsBuiltInAdministrator(targetSid))
        {
            return "The built-in Administrator account is protected and cannot be deleted.";
        }

        return LastAdminGuard(targetSid, users, "deleting");
    }

    /// <summary>Returns a refusal reason, or null when the disable is permitted.</summary>
    public static string? ValidateDisable(string targetSid, IReadOnlyCollection<LocalAccountView> users)
    {
        if (IsBuiltInAdministrator(targetSid))
        {
            return "The built-in Administrator account is protected and cannot be disabled.";
        }

        return LastAdminGuard(targetSid, users, "disabling");
    }

    /// <summary>
    /// Returns a refusal reason, or null when the demotion (remove from Administrators)
    /// is permitted.
    /// </summary>
    public static string? ValidateDemote(string targetSid, IReadOnlyCollection<LocalAccountView> users) =>
        LastAdminGuard(targetSid, users, "demoting");

    private static string? LastAdminGuard(
        string targetSid, IReadOnlyCollection<LocalAccountView> users, string verb)
    {
        var target = users.FirstOrDefault(
            u => string.Equals(u.Sid, targetSid, StringComparison.OrdinalIgnoreCase));

        // If the target is not a currently-enabled administrator, the operation cannot
        // reduce the enabled-admin count to zero. If we cannot see the target at all,
        // defer to the agent's authoritative live re-check rather than guess.
        if (target is null || !target.IsAdministrator || !target.Enabled)
        {
            return null;
        }

        var otherEnabledAdmins = users.Count(u =>
            u.IsAdministrator
            && u.Enabled
            && !string.Equals(u.Sid, targetSid, StringComparison.OrdinalIgnoreCase));

        return otherEnabledAdmins == 0
            ? $"Refusing: {verb} '{target.Username}' would leave this device with no enabled administrator."
            : null;
    }
}
