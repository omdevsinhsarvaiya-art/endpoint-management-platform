using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class DeviceLocalAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_local_groups",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    members_json = table.Column<string>(type: "jsonb", nullable: false),
                    member_count = table.Column<int>(type: "integer", nullable: false),
                    collected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_local_groups", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_local_groups_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_local_users",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    full_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    password_required = table.Column<bool>(type: "boolean", nullable: false),
                    password_expires = table.Column<bool>(type: "boolean", nullable: false),
                    last_logon = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_local_administrator = table.Column<bool>(type: "boolean", nullable: false),
                    collected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_local_users", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_local_users_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_local_groups_device_id_sid",
                schema: "endpoint_platform",
                table: "device_local_groups",
                columns: new[] { "device_id", "sid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_local_users_device_id_sid",
                schema: "endpoint_platform",
                table: "device_local_users",
                columns: new[] { "device_id", "sid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_local_users_is_local_administrator",
                schema: "endpoint_platform",
                table: "device_local_users",
                column: "is_local_administrator",
                filter: "is_local_administrator");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_local_groups",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "device_local_users",
                schema: "endpoint_platform");
        }
    }
}
