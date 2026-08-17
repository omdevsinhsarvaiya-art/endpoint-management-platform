using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <summary>
    /// Makes <c>audit_log_entries</c> append-only at the database level.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the control that actually enforces audit immutability. The
    /// application-side <c>AuditImmutabilityInterceptor</c> only catches honest
    /// mistakes; a trigger applies to every session, including one opened by an
    /// attacker who has obtained the application's database credential.
    /// </para>
    /// <para>
    /// Three triggers are needed, not one. Row-level BEFORE UPDATE and BEFORE
    /// DELETE triggers do not fire for TRUNCATE - TRUNCATE bypasses row triggers
    /// entirely - so a statement-level BEFORE TRUNCATE trigger is required as well.
    /// Without it, "DELETE the audit log" simply becomes "TRUNCATE the audit log".
    /// </para>
    /// <para>
    /// A table owner or superuser can still drop the trigger. Defending against
    /// that requires shipping audit records off-box to append-only storage, which
    /// is recorded as a known limitation in docs/threat-model.md.
    /// </para>
    /// </remarks>
    public partial class AuditTrailImmutability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION endpoint_platform.reject_audit_log_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    RAISE EXCEPTION
                        'audit_log_entries is append-only; % is not permitted on this table', TG_OP
                        USING ERRCODE = 'restrict_violation',
                              HINT = 'Audit records may only be inserted. See docs/threat-model.md.';
                END;
                $function$;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER trg_audit_log_entries_reject_update
                    BEFORE UPDATE ON endpoint_platform.audit_log_entries
                    FOR EACH ROW
                    EXECUTE FUNCTION endpoint_platform.reject_audit_log_mutation();
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER trg_audit_log_entries_reject_delete
                    BEFORE DELETE ON endpoint_platform.audit_log_entries
                    FOR EACH ROW
                    EXECUTE FUNCTION endpoint_platform.reject_audit_log_mutation();
                """);

            // Statement-level: TRUNCATE does not fire row triggers.
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_audit_log_entries_reject_truncate
                    BEFORE TRUNCATE ON endpoint_platform.audit_log_entries
                    FOR EACH STATEMENT
                    EXECUTE FUNCTION endpoint_platform.reject_audit_log_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP TRIGGER IF EXISTS trg_audit_log_entries_reject_truncate ON endpoint_platform.audit_log_entries;");
            migrationBuilder.Sql(
                "DROP TRIGGER IF EXISTS trg_audit_log_entries_reject_delete ON endpoint_platform.audit_log_entries;");
            migrationBuilder.Sql(
                "DROP TRIGGER IF EXISTS trg_audit_log_entries_reject_update ON endpoint_platform.audit_log_entries;");
            migrationBuilder.Sql(
                "DROP FUNCTION IF EXISTS endpoint_platform.reject_audit_log_mutation();");
        }
    }
}
