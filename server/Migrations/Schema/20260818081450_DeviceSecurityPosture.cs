using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class DeviceSecurityPosture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_security_posture",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    defender_antivirus_enabled = table.Column<bool>(type: "boolean", nullable: true),
                    defender_realtime_protection_enabled = table.Column<bool>(type: "boolean", nullable: true),
                    defender_signature_age_days = table.Column<int>(type: "integer", nullable: true),
                    firewall_domain_enabled = table.Column<bool>(type: "boolean", nullable: true),
                    firewall_private_enabled = table.Column<bool>(type: "boolean", nullable: true),
                    firewall_public_enabled = table.Column<bool>(type: "boolean", nullable: true),
                    secure_boot_enabled = table.Column<bool>(type: "boolean", nullable: true),
                    tpm_present = table.Column<bool>(type: "boolean", nullable: true),
                    tpm_enabled = table.Column<bool>(type: "boolean", nullable: true),
                    tpm_spec_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    bit_locker_system_drive_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    local_administrator_count = table.Column<int>(type: "integer", nullable: true),
                    collected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_security_posture", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_security_posture_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_security_posture_device_id",
                schema: "endpoint_platform",
                table: "device_security_posture",
                column: "device_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_security_posture",
                schema: "endpoint_platform");
        }
    }
}
