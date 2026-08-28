using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class BitLockerRecoveryEscrow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bitlocker_recovery_escrows",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    volume_device_identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    key_protector_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    drive_letter = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    sealed_recovery_password = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    key_version = table.Column<int>(type: "integer", nullable: false),
                    escrowed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    escrowed_by_display = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    escrowed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    superseded_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    superseded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revealed_count = table.Column<int>(type: "integer", nullable: false),
                    last_revealed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_revealed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_by_display = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bitlocker_recovery_escrows", x => x.id);
                    table.ForeignKey(
                        name: "fk_bitlocker_recovery_escrows_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bitlocker_recovery_escrows_device_id",
                schema: "endpoint_platform",
                table: "bitlocker_recovery_escrows",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_bitlocker_recovery_escrows_superseded_by",
                schema: "endpoint_platform",
                table: "bitlocker_recovery_escrows",
                column: "superseded_by_id");

            migrationBuilder.CreateIndex(
                name: "ux_bitlocker_recovery_escrows_active",
                schema: "endpoint_platform",
                table: "bitlocker_recovery_escrows",
                columns: new[] { "device_id", "volume_device_identifier", "key_protector_id" },
                unique: true,
                filter: "is_active");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bitlocker_recovery_escrows",
                schema: "endpoint_platform");
        }
    }
}
