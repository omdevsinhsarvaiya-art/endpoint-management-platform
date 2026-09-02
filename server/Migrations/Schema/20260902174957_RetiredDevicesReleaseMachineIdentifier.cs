using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class RetiredDevicesReleaseMachineIdentifier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_devices_organization_id_machine_identifier",
                schema: "endpoint_platform",
                table: "devices");

            migrationBuilder.CreateIndex(
                name: "ix_devices_organization_id_machine_identifier",
                schema: "endpoint_platform",
                table: "devices",
                columns: new[] { "organization_id", "machine_identifier" },
                unique: true,
                filter: "status = 'Active'");
        }

        /// <inheritdoc />
        /// <remarks>
        /// Refuses rather than corrupts. Once a retired machine has re-enrolled, the
        /// retired row and the new active row legitimately share a machine
        /// identifier -- which the unfiltered index this restores cannot represent.
        /// Postgres would fail the index build with a duplicate-key error naming
        /// nothing useful; this fails first with a message that says what to do.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    conflicts bigint;
                BEGIN
                    SELECT count(*) INTO conflicts
                    FROM (
                        SELECT organization_id, machine_identifier
                        FROM endpoint_platform.devices
                        GROUP BY organization_id, machine_identifier
                        HAVING count(*) > 1
                    ) AS duplicated;

                    IF conflicts > 0 THEN
                        RAISE EXCEPTION
                            'Cannot roll back: % machine identifier(s) are shared by a retired device and its re-enrolled replacement. Restoring the unfiltered unique index would require deleting device history. Reactivate or remove the duplicates deliberately before rolling back.',
                            conflicts;
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "ix_devices_organization_id_machine_identifier",
                schema: "endpoint_platform",
                table: "devices");

            migrationBuilder.CreateIndex(
                name: "ix_devices_organization_id_machine_identifier",
                schema: "endpoint_platform",
                table: "devices",
                columns: new[] { "organization_id", "machine_identifier" },
                unique: true);
        }
    }
}
