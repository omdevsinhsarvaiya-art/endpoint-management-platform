using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class DeviceBitLocker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_bitlocker_status",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    availability = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    collected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_bitlocker_status", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_bitlocker_status_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_bitlocker_volumes",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    drive_letter = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    persistent_volume_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    volume_type = table.Column<int>(type: "integer", nullable: true),
                    conversion_status = table.Column<int>(type: "integer", nullable: true),
                    protection_status = table.Column<int>(type: "integer", nullable: true),
                    encryption_percentage = table.Column<int>(type: "integer", nullable: true),
                    encryption_method = table.Column<int>(type: "integer", nullable: true),
                    has_recovery_password_protector = table.Column<bool>(type: "boolean", nullable: true),
                    recovery_protector_ids = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    collected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_bitlocker_volumes", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_bitlocker_volumes_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_device_bitlocker_status_device_id",
                schema: "endpoint_platform",
                table: "device_bitlocker_status",
                column: "device_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_bitlocker_volumes_device_id",
                schema: "endpoint_platform",
                table: "device_bitlocker_volumes",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_bitlocker_volumes_status",
                schema: "endpoint_platform",
                table: "device_bitlocker_volumes",
                columns: new[] { "conversion_status", "protection_status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_bitlocker_status",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "device_bitlocker_volumes",
                schema: "endpoint_platform");
        }
    }
}
