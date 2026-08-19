using System.Collections.Frozen;

namespace EndpointPlatform.Domain.Authorization;

/// <summary>
/// The four built-in roles and the permissions each one grants.
/// </summary>
/// <remarks>
/// <para>
/// These definitions are the seed data for the <c>roles</c> and
/// <c>role_permissions</c> tables. They are re-applied on every startup so a role
/// cannot drift away from its documented meaning by accident, but built-in roles
/// are never deleted and their permission sets are the only thing reconciled.
/// </para>
/// <para>
/// Note what each role deliberately does NOT get:
/// Helpdesk cannot change account type, delete accounts, deploy software or
/// assign policies. Auditor is strictly read-only — it holds no permission whose
/// name implies mutation. Those omissions are asserted by
/// <c>SystemRoleTests</c> so a future edit cannot quietly widen them.
/// </para>
/// </remarks>
public static class SystemRoles
{
    public const string SuperAdministrator = "super_administrator";
    public const string ItAdministrator = "it_administrator";
    public const string Helpdesk = "helpdesk";
    public const string Auditor = "auditor";

    /// <summary>
    /// Super Administrator holds every permission in the catalogue by definition,
    /// computed rather than listed so a new permission can never be accidentally
    /// withheld from it.
    /// </summary>
    private static IReadOnlyList<string> SuperAdministratorPermissions =>
        Permissions.AllKeys.Order(StringComparer.Ordinal).ToArray();

    private static readonly string[] ItAdministratorPermissions =
    [
        Permissions.Device.View,
        Permissions.Device.Restart,
        Permissions.Device.Shutdown,
        Permissions.Device.Lock,
        Permissions.Device.SignOutUser,
        Permissions.Device.Enroll,
        Permissions.Device.Retire,
        Permissions.Device.RefreshInventory,
        Permissions.LocalUser.View,
        Permissions.LocalUser.Create,
        Permissions.LocalUser.Delete,
        Permissions.LocalUser.Disable,
        Permissions.LocalUser.ResetPassword,
        Permissions.LocalUser.ChangeType,
        Permissions.LocalUser.ForcePasswordChange,
        Permissions.Group.View,
        Permissions.Group.Manage,
        Permissions.Software.View,
        Permissions.Software.Deploy,
        Permissions.Policy.View,
        Permissions.Policy.Create,
        Permissions.Policy.Assign,
        Permissions.Task.View,
        Permissions.Task.Execute,
        Permissions.Audit.View,
        Permissions.Platform.EnrollmentTokenView,
        Permissions.Platform.EnrollmentTokenIssue,
        Permissions.Platform.EnrollmentTokenRevoke,
    ];

    private static readonly string[] HelpdeskPermissions =
    [
        Permissions.Device.View,
        Permissions.Device.Restart,
        Permissions.Device.Lock,
        Permissions.Device.RefreshInventory,
        Permissions.LocalUser.View,
        Permissions.LocalUser.Disable,
        Permissions.LocalUser.ResetPassword,
        Permissions.LocalUser.ForcePasswordChange,
        Permissions.Group.View,
        Permissions.Software.View,
        Permissions.Policy.View,
        Permissions.Task.View,
    ];

    private static readonly string[] AuditorPermissions =
    [
        Permissions.Device.View,
        Permissions.LocalUser.View,
        Permissions.Group.View,
        Permissions.Software.View,
        Permissions.Policy.View,
        Permissions.Task.View,
        Permissions.Audit.View,
        Permissions.Platform.UserView,
        Permissions.Platform.EnrollmentTokenView,
    ];

    public static FrozenDictionary<string, SystemRoleDefinition> All { get; } =
        new SystemRoleDefinition[]
        {
            new(
                SuperAdministrator,
                "Super Administrator",
                "Unrestricted access to every platform capability, including platform user and role management.",
                SuperAdministratorPermissions),
            new(
                ItAdministrator,
                "IT Administrator",
                "Day-to-day endpoint administration: devices, local accounts, groups, software, policies and tasks.",
                ItAdministratorPermissions),
            new(
                Helpdesk,
                "Helpdesk",
                "First-line support: view everything operational, restart and lock devices, reset and disable local accounts.",
                HelpdeskPermissions),
            new(
                Auditor,
                "Auditor",
                "Strictly read-only access, including the audit log. Holds no permission that mutates state.",
                AuditorPermissions),
        }.ToFrozenDictionary(r => r.Key, StringComparer.Ordinal);
}

public sealed record SystemRoleDefinition(
    string Key,
    string DisplayName,
    string Description,
    IReadOnlyList<string> PermissionKeys);
