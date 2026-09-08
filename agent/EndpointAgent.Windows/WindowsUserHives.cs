using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

namespace EndpointAgent.Windows;

/// <summary>One loaded user profile hive: who it belongs to, and how to read it.</summary>
/// <param name="Sid">The account's SID -- the stable identity, since names are renameable.</param>
/// <param name="Account">The resolved account name, or the SID when it cannot be resolved.</param>
public readonly record struct LoadedUserHive(string Sid, string Account);

/// <summary>
/// The user profile hives Windows currently has mounted.
/// </summary>
/// <remarks>
/// <para>
/// Shared by every per-user discovery source, so they agree on which hives exist,
/// which are people, and what to call them. Before this, that logic lived inside
/// the uninstall-registry reader; a second source copying it would have been a
/// second place for the SYSTEM-profile mistake to come back.
/// </para>
/// <para>
/// <b>Only mounted hives.</b> A user who is fully signed out has none, and their
/// per-user software is not reported until they next sign in. Reading it anyway
/// would mean <c>RegLoadKey</c> on a profile the agent does not own, which can
/// fail on a locked or roaming profile and, if a hive were left mounted, block
/// that user's next logon. Under-reporting a signed-out user is the safer
/// failure and is a deliberate, documented choice.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsUserHives
{
    /// <summary>
    /// Every mounted hive that belongs to a person.
    /// </summary>
    /// <remarks>
    /// HKEY_USERS holds one subkey per mounted hive, named by SID. The well-known
    /// service SIDs (S-1-5-18/19/20 -- SYSTEM and the two service accounts) are
    /// not people, and every hive may also appear with a <c>_Classes</c> suffix
    /// holding COM and package registration rather than a user's own settings.
    /// Neither is a profile in its own right.
    /// </remarks>
    public static IReadOnlyList<LoadedUserHive> Loaded()
    {
        try
        {
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);

            return users.GetSubKeyNames()
                .Where(IsRealUserSid)
                .Select(sid => new LoadedUserHive(sid, ResolveAccountName(sid)))
                .ToArray();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Whether this HKEY_USERS subkey is a human's profile hive.
    /// </summary>
    /// <remarks>
    /// Real accounts are S-1-5-21-... (local or domain) or S-1-12-1-... (Entra).
    /// </remarks>
    public static bool IsRealUserSid(string sid) =>
        !sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)
        && (sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)
            || sid.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase));

    /// <summary>The account a SID names, falling back to the SID itself.</summary>
    /// <remarks>
    /// A deleted or unresolvable account still had software installed, so the SID
    /// is reported rather than dropping the entry: an unattributed application is
    /// more useful than a missing one.
    /// </remarks>
    public static string ResolveAccountName(string sid)
    {
        try
        {
            return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return sid;
        }
    }

    /// <summary>
    /// Opens a subkey of one user's hive, or null when it is absent or unreadable.
    /// </summary>
    /// <remarks>
    /// The caller disposes. <paramref name="classes"/> selects the companion
    /// <c>&lt;SID&gt;_Classes</c> hive, which is where package registration lives.
    /// </remarks>
    public static RegistryKey? OpenUserSubKey(string sid, string subKeyPath, bool classes = false)
    {
        try
        {
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
            return users.OpenSubKey(classes ? sid + "_Classes" : sid)?.OpenSubKey(subKeyPath);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
