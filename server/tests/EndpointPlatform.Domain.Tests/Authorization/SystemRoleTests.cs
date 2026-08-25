using EndpointPlatform.Domain.Authorization;

namespace EndpointPlatform.Domain.Tests.Authorization;

/// <summary>
/// Locks down what each built-in role may and may not do.
/// </summary>
/// <remarks>
/// These are security tests, not coverage tests. The failure they exist to prevent
/// is a future edit quietly widening a role - adding <c>device.shutdown</c> to
/// Helpdesk, or any mutating permission to Auditor - which would not break a single
/// functional test but would silently change who can take an endpoint offline.
/// </remarks>
public sealed class SystemRoleTests
{
    [Fact]
    public void All_defines_exactly_the_four_documented_roles()
    {
        SystemRoles.All.Keys.ShouldBe(
            [
                SystemRoles.SuperAdministrator,
                SystemRoles.ItAdministrator,
                SystemRoles.Helpdesk,
                SystemRoles.Auditor,
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void Every_role_grants_only_permissions_that_exist_in_the_catalogue()
    {
        foreach (var (roleKey, definition) in SystemRoles.All)
        {
            foreach (var permissionKey in definition.PermissionKeys)
            {
                Permissions.IsKnown(permissionKey).ShouldBeTrue(
                    $"Role '{roleKey}' grants '{permissionKey}', which is not in the permission catalogue. " +
                    "Seeding would throw at startup.");
            }
        }
    }

    [Fact]
    public void Super_administrator_holds_every_permission()
    {
        var superAdmin = SystemRoles.All[SystemRoles.SuperAdministrator];

        superAdmin.PermissionKeys.ShouldBe(Permissions.AllKeys, ignoreOrder: true);
    }

    /// <summary>
    /// Auditor must be read-only. Rather than listing what it may not have (which a
    /// new permission would silently escape), this asserts the inverse: every
    /// permission Auditor holds must be one that is not marked high-risk and whose
    /// key denotes a read.
    /// </summary>
    [Fact]
    public void Auditor_holds_no_mutating_permission()
    {
        var auditor = SystemRoles.All[SystemRoles.Auditor];
        var catalogue = Permissions.All.ToDictionary(p => p.Key, StringComparer.Ordinal);

        foreach (var key in auditor.PermissionKeys)
        {
            var definition = catalogue[key];

            definition.HighRisk.ShouldBeFalse(
                $"Auditor is a read-only role but holds high-risk permission '{key}'.");

            key.ShouldEndWith(
                ".view",
                Case.Sensitive,
                $"Auditor holds '{key}', which is not a view permission. Auditor must not mutate state.");
        }
    }

    [Fact]
    public void Auditor_can_read_the_audit_log()
    {
        SystemRoles.All[SystemRoles.Auditor].PermissionKeys.ShouldContain(Permissions.Audit.View);
    }

    [Theory]
    [InlineData(Permissions.LocalUser.ChangeType)]
    [InlineData(Permissions.LocalUser.Delete)]
    [InlineData(Permissions.LocalUser.Create)]
    [InlineData(Permissions.Device.Shutdown)]
    [InlineData(Permissions.Device.Retire)]
    [InlineData(Permissions.Group.Manage)]
    [InlineData(Permissions.Software.Deploy)]
    [InlineData(Permissions.Policy.Assign)]
    [InlineData(Permissions.Task.Execute)]
    // Helpdesk can see which USB device is on which machine, because that is
    // half of every "my drive isn't showing up" call. Opening a data path off
    // an endpoint is a security decision and stays with IT Administrator.
    [InlineData(Permissions.Usb.Manage)]
    [InlineData(Permissions.Platform.UserManage)]
    [InlineData(Permissions.Platform.RoleManage)]
    [InlineData(Permissions.Platform.SettingsManage)]
    public void Helpdesk_is_denied_high_impact_permissions(string permissionKey)
    {
        SystemRoles.All[SystemRoles.Helpdesk].PermissionKeys.ShouldNotContain(
            permissionKey,
            $"Helpdesk must not be able to perform '{permissionKey}'.");
    }

    [Theory]
    [InlineData(Permissions.Platform.UserManage)]
    [InlineData(Permissions.Platform.RoleManage)]
    [InlineData(Permissions.Platform.SettingsManage)]
    public void It_administrator_cannot_manage_platform_users_or_roles(string permissionKey)
    {
        // Separation of duties: an IT administrator manages endpoints, not who is
        // allowed to manage endpoints. Granting themselves more access must require
        // a Super Administrator.
        SystemRoles.All[SystemRoles.ItAdministrator].PermissionKeys.ShouldNotContain(permissionKey);
    }

    [Fact]
    public void It_administrator_can_perform_core_endpoint_administration()
    {
        var itAdmin = SystemRoles.All[SystemRoles.ItAdministrator].PermissionKeys;

        itAdmin.ShouldContain(Permissions.Device.View);
        itAdmin.ShouldContain(Permissions.Device.Restart);
        itAdmin.ShouldContain(Permissions.LocalUser.ChangeType);
        itAdmin.ShouldContain(Permissions.Group.Manage);
        itAdmin.ShouldContain(Permissions.Software.Deploy);
        itAdmin.ShouldContain(Permissions.Task.Execute);
    }

    /// <summary>
    /// Only IT Administrator and Super Administrator may open USB storage access.
    /// </summary>
    /// <remarks>
    /// Stated as a whole-catalogue assertion rather than one negative per role, so
    /// that a role added later cannot pick up <c>usb.manage</c> without this
    /// failing. The permission's entire effect is to make a removable disk
    /// readable on a managed endpoint, which is a data-egress decision.
    /// </remarks>
    [Fact]
    public void Only_administrators_can_grant_usb_storage_access()
    {
        var holders = SystemRoles.All
            .Where(r => r.Value.PermissionKeys.Contains(Permissions.Usb.Manage, StringComparer.Ordinal))
            .Select(r => r.Key)
            .ToArray();

        holders.ShouldBe([SystemRoles.SuperAdministrator, SystemRoles.ItAdministrator], ignoreOrder: true);
    }

    [Fact]
    public void Usb_visibility_is_read_only_and_therefore_safe_for_auditor()
    {
        var catalogue = Permissions.All.ToDictionary(p => p.Key, StringComparer.Ordinal);

        catalogue[Permissions.Usb.View].HighRisk.ShouldBeFalse();
        catalogue[Permissions.Usb.Manage].HighRisk.ShouldBeTrue();

        SystemRoles.All[SystemRoles.Auditor].PermissionKeys.ShouldContain(Permissions.Usb.View);
    }

    [Fact]
    public void No_role_definition_contains_duplicate_permissions()
    {
        foreach (var (roleKey, definition) in SystemRoles.All)
        {
            definition.PermissionKeys.Distinct(StringComparer.Ordinal).Count()
                .ShouldBe(definition.PermissionKeys.Count, $"Role '{roleKey}' lists a permission twice.");
        }
    }
}
