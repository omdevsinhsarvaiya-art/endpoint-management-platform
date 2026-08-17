using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EndpointPlatform.Migrations;

/// <summary>
/// Grants the restricted runtime database role exactly the privileges the APIs need.
/// </summary>
/// <remarks>
/// <para>
/// Runs as part of the migration job, after migrations, because grants must follow
/// the objects they apply to. It is idempotent and re-applied on every deployment,
/// so a table added by a later migration cannot silently end up unreachable - or,
/// worse, reachable with more privilege than intended.
/// </para>
/// <para>
/// The privilege split that matters:
/// </para>
/// <list type="bullet">
///   <item>Ordinary tables: SELECT, INSERT, UPDATE, DELETE.</item>
///   <item><c>audit_log_entries</c>: SELECT and INSERT only. UPDATE, DELETE and
///   TRUNCATE are revoked, so even a full compromise of the application credential
///   cannot rewrite the audit trail.</item>
///   <item>No CREATE on the schema: the runtime role cannot perform DDL, so it
///   cannot drop the audit triggers that back up the revoked privileges.</item>
/// </list>
/// <para>
/// The role name is a configuration value, never concatenated into SQL. It travels
/// as a query parameter into <c>set_config</c> and is re-emitted through
/// <c>format(%I)</c>, which quotes it as an identifier. That is what keeps a
/// hostile <c>Database:RuntimeRoleName</c> from becoming SQL injection in a job
/// that runs as the database owner.
/// </para>
/// </remarks>
public sealed class RuntimeGrantsApplier(
    EndpointPlatformDbContext dbContext,
    ILogger<RuntimeGrantsApplier> logger)
{
    private const string RoleSettingKey = "endpointplatform.runtime_role";

    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly ILogger<RuntimeGrantsApplier> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public async Task ApplyAsync(string runtimeRoleName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoleName);

        var roleExists = await RoleExistsAsync(runtimeRoleName, cancellationToken);

        if (!roleExists)
        {
            // Not fatal: a developer may be running everything as the owner role.
            // But it must be loud, because it means the audit trail is not protected
            // by the privilege split it is supposed to rely on.
            _logger.LogWarning(
                "Runtime database role '{Role}' does not exist; skipping grants. The APIs will have to "
                + "connect as the schema owner, which means the database-level restriction preventing the "
                + "application from modifying audit records is NOT in effect. Create the role (see "
                + "infra/postgres/init/01-create-app-role.sh) and re-run the migration job.",
                runtimeRoleName);
            return;
        }

        _logger.LogInformation("Applying runtime grants to database role '{Role}'...", runtimeRoleName);

        // set_config takes the role name as a bound parameter; the DO block then
        // reads it back and quotes it with format(%I). No string concatenation.
        await _dbContext.Database.ExecuteSqlRawAsync(
            "SELECT set_config({0}, {1}, false);",
            [RoleSettingKey, runtimeRoleName],
            cancellationToken);

        await _dbContext.Database.ExecuteSqlRawAsync(GrantsScript, cancellationToken);

        _logger.LogInformation(
            "Runtime grants applied. Role '{Role}' has SELECT/INSERT only on audit_log_entries "
            + "and cannot perform DDL.",
            runtimeRoleName);
    }

    private async Task<bool> RoleExistsAsync(string roleName, CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @role);";

        var parameter = new NpgsqlParameter("role", roleName);
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }

    private const string GrantsScript = $"""
        DO $grants$
        DECLARE
            v_role text := current_setting('{RoleSettingKey}');
            v_schema text := '{EndpointPlatformDbContext.Schema}';
        BEGIN
            -- Reach the schema, but never create objects in it: no DDL rights means
            -- the role cannot drop the audit-immutability triggers.
            EXECUTE format('GRANT USAGE ON SCHEMA %I TO %I', v_schema, v_role);
            EXECUTE format('REVOKE CREATE ON SCHEMA %I FROM %I', v_schema, v_role);

            -- Ordinary application tables: full DML.
            EXECUTE format(
                'GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO %I',
                v_schema, v_role);

            EXECUTE format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA %I TO %I', v_schema, v_role);

            -- Tables created by FUTURE migrations inherit the same grants, so a new
            -- table is never unreachable and never over-privileged.
            EXECUTE format(
                'ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I',
                v_schema, v_role);
            EXECUTE format(
                'ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT USAGE, SELECT ON SEQUENCES TO %I',
                v_schema, v_role);

            -- The audit trail is the exception: append and read, nothing else.
            -- This runs AFTER the blanket grant above, so it takes precedence.
            EXECUTE format(
                'REVOKE ALL ON TABLE %I.audit_log_entries FROM %I',
                v_schema, v_role);
            EXECUTE format(
                'GRANT SELECT, INSERT ON TABLE %I.audit_log_entries TO %I',
                v_schema, v_role);

            -- The runtime role does not migrate; it only needs to read the history
            -- table if a diagnostic asks which migrations are applied.
            EXECUTE format(
                'REVOKE INSERT, UPDATE, DELETE ON TABLE %I.__ef_migrations_history FROM %I',
                v_schema, v_role);
        END
        $grants$;
        """;
}
