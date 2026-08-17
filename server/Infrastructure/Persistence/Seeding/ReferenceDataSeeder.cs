using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Persistence.Seeding;

/// <summary>
/// Reconciles the database's reference data with the code-defined catalogue.
/// </summary>
/// <remarks>
/// <para>
/// Runs on every deployment and is idempotent. The catalogue in
/// <see cref="Permissions"/> and <see cref="SystemRoles"/> is authoritative:
/// permissions and built-in role grants are brought into line with it, so a role's
/// meaning cannot drift from what the code and the documentation say it is.
/// </para>
/// <para>
/// Deliberately NOT seeded here: any account that can sign in. Creating a
/// bootstrap administrator requires a credential, and a credential baked into
/// source or into a container image is a hardcoded secret. Bootstrap is a separate,
/// explicit operator step introduced with authentication in Phase 3.
/// </para>
/// <para>
/// Permissions removed from the catalogue are reported but not deleted. Deleting
/// one would cascade away the role grants that reference it, silently changing
/// what every affected role can do; retiring a permission is a deliberate
/// migration, not a side effect of startup.
/// </para>
/// </remarks>
public sealed class ReferenceDataSeeder(
    EndpointPlatformDbContext dbContext,
    IOptions<SeedOptions> options,
    ILogger<ReferenceDataSeeder> logger)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly SeedOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    private readonly ILogger<ReferenceDataSeeder> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public async Task<SeedResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        var permissionsChanged = await SeedPermissionsAsync(cancellationToken);
        var rolesChanged = await SeedBuiltInRolesAsync(cancellationToken);
        var organizationCreated = await EnsureDefaultOrganizationAsync(cancellationToken);

        var changes = permissionsChanged + rolesChanged + (organizationCreated ? 1 : 0);

        if (changes > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        var result = new SeedResult(permissionsChanged, rolesChanged, organizationCreated);

        _logger.LogInformation(
            "Reference data seeding complete. Permissions changed: {PermissionChanges}, " +
            "role grants changed: {RoleChanges}, default organization created: {OrganizationCreated}.",
            result.PermissionChanges,
            result.RoleGrantChanges,
            result.DefaultOrganizationCreated);

        return result;
    }

    private async Task<int> SeedPermissionsAsync(CancellationToken cancellationToken)
    {
        var existing = await _dbContext.Permissions
            .ToDictionaryAsync(p => p.Key, StringComparer.Ordinal, cancellationToken);

        var changes = 0;

        foreach (var definition in Permissions.All)
        {
            if (existing.TryGetValue(definition.Key, out var permission))
            {
                if (permission.Category != definition.Category
                    || permission.Description != definition.Description
                    || permission.IsHighRisk != definition.HighRisk)
                {
                    permission.UpdateMetadata(definition.Category, definition.Description, definition.HighRisk);
                    changes++;
                }

                continue;
            }

            _dbContext.Permissions.Add(
                new Permission(definition.Key, definition.Category, definition.Description, definition.HighRisk));
            changes++;
        }

        var orphaned = existing.Keys.Where(key => !Permissions.IsKnown(key)).ToArray();

        if (orphaned.Length > 0)
        {
            // Left in place on purpose - see the remarks on this class.
            _logger.LogWarning(
                "{Count} permission(s) exist in the database but not in the code catalogue and were left " +
                "untouched: {Keys}. Retiring a permission requires an explicit migration.",
                orphaned.Length,
                string.Join(", ", orphaned));
        }

        return changes;
    }

    private async Task<int> SeedBuiltInRolesAsync(CancellationToken cancellationToken)
    {
        // Permissions added moments ago are still only tracked, so resolve ids from
        // the change tracker as well as the database.
        var permissionIdByKey = await _dbContext.Permissions
            .ToDictionaryAsync(p => p.Key, p => p.Id, StringComparer.Ordinal, cancellationToken);

        foreach (var entry in _dbContext.ChangeTracker.Entries<Permission>()
                     .Where(e => e.State == EntityState.Added))
        {
            permissionIdByKey[entry.Entity.Key] = entry.Entity.Id;
        }

        var existingRoles = await _dbContext.Roles
            .Include(r => r.Permissions)
            .Where(r => r.IsBuiltIn)
            .ToDictionaryAsync(r => r.Key, StringComparer.Ordinal, cancellationToken);

        var changes = 0;

        foreach (var (key, definition) in SystemRoles.All)
        {
            if (!existingRoles.TryGetValue(key, out var role))
            {
                role = Role.CreateBuiltIn(definition.Key, definition.DisplayName, definition.Description);
                _dbContext.Roles.Add(role);
                changes++;
            }

            var desired = definition.PermissionKeys
                .Select(permissionKey =>
                {
                    if (!permissionIdByKey.TryGetValue(permissionKey, out var id))
                    {
                        // A role referencing a permission the catalogue does not define is a
                        // coding error; failing here beats silently granting less than documented.
                        throw new InvalidOperationException(
                            $"Built-in role '{key}' references unknown permission '{permissionKey}'.");
                    }

                    return id;
                })
                .ToHashSet();

            var current = role.Permissions.Select(p => p.PermissionId).ToHashSet();

            foreach (var permissionId in desired.Except(current))
            {
                role.GrantPermission(permissionId);
                changes++;
            }

            foreach (var permissionId in current.Except(desired))
            {
                role.RevokePermission(permissionId);
                changes++;
            }
        }

        return changes;
    }

    private async Task<bool> EnsureDefaultOrganizationAsync(CancellationToken cancellationToken)
    {
        var exists = await _dbContext.Organizations
            .AnyAsync(o => o.Slug == _options.DefaultOrganizationSlug, cancellationToken);

        if (exists)
        {
            return false;
        }

        _dbContext.Organizations.Add(
            new Organization(_options.DefaultOrganizationName, _options.DefaultOrganizationSlug));

        _logger.LogInformation(
            "Created default organization '{Slug}'.",
            _options.DefaultOrganizationSlug);

        return true;
    }
}

/// <summary>Summary of what a seeding run changed.</summary>
public sealed record SeedResult(int PermissionChanges, int RoleGrantChanges, bool DefaultOrganizationCreated);
