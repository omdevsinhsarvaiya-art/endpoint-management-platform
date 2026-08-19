using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class LocalAccountManagementAndDeviceScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "has_all_device_scope",
                schema: "endpoint_platform",
                table: "platform_users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "admin_device_scopes",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_admin_device_scopes", x => x.id);
                    table.ForeignKey(
                        name: "fk_admin_device_scopes_device_groups_device_group_id",
                        column: x => x.device_group_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "device_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_admin_device_scopes_platform_users_platform_user_id",
                        column: x => x.platform_user_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "platform_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_admin_device_scopes_device_group_id",
                schema: "endpoint_platform",
                table: "admin_device_scopes",
                column: "device_group_id");

            // Every administrator that predates device scoping keeps the authority they
            // already had. The column defaults to false so NEW administrators are
            // deny-by-default, but applying that default to existing accounts would
            // silently revoke every operator's access at upgrade time.
            migrationBuilder.Sql(
                "UPDATE endpoint_platform.platform_users SET has_all_device_scope = TRUE;");

            migrationBuilder.CreateIndex(
                name: "ix_admin_device_scopes_user_id_group_id",
                schema: "endpoint_platform",
                table: "admin_device_scopes",
                columns: new[] { "platform_user_id", "device_group_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_device_scopes",
                schema: "endpoint_platform");

            migrationBuilder.DropColumn(
                name: "has_all_device_scope",
                schema: "endpoint_platform",
                table: "platform_users");
        }
    }
}
