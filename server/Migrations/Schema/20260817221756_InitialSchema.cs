using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "endpoint_platform");

            migrationBuilder.CreateTable(
                name: "organizations",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "permissions",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    is_high_risk = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_permissions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_log_entries",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_display = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    device_display = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    target_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    target_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    target_display = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    previous_state = table.Column<string>(type: "jsonb", nullable: true),
                    new_state = table.Column<string>(type: "jsonb", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    required_permission = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    source_ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_audit_log_entries_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "platform_users",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    normalized_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    password_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    security_stamp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_sign_in_count = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_system_account = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_users", x => x.id);
                    table.ForeignKey(
                        name: "fk_platform_users_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    is_built_in = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.id);
                    table.ForeignKey(
                        name: "fk_roles_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "platform_user_roles",
                schema: "endpoint_platform",
                columns: table => new
                {
                    platform_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_user_roles", x => new { x.platform_user_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_platform_user_roles_platform_users_platform_user_id",
                        column: x => x.platform_user_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "platform_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_platform_user_roles_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                schema: "endpoint_platform",
                columns: table => new
                {
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_permissions", x => new { x.role_id, x.permission_id });
                    table.ForeignKey(
                        name: "fk_role_permissions_permissions_permission_id",
                        column: x => x.permission_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "permissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_role_permissions_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_entries_actor_id_occurred_at",
                schema: "endpoint_platform",
                table: "audit_log_entries",
                columns: new[] { "actor_id", "occurred_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_entries_correlation_id",
                schema: "endpoint_platform",
                table: "audit_log_entries",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_entries_device_id_occurred_at",
                schema: "endpoint_platform",
                table: "audit_log_entries",
                columns: new[] { "device_id", "occurred_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_entries_failures",
                schema: "endpoint_platform",
                table: "audit_log_entries",
                columns: new[] { "organization_id", "occurred_at" },
                descending: new[] { false, true },
                filter: "result <> 'Success'");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_entries_organization_id_action_occurred_at",
                schema: "endpoint_platform",
                table: "audit_log_entries",
                columns: new[] { "organization_id", "action", "occurred_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_organizations_slug",
                schema: "endpoint_platform",
                table: "organizations",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_permissions_key",
                schema: "endpoint_platform",
                table: "permissions",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_platform_user_roles_role_id",
                schema: "endpoint_platform",
                table: "platform_user_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_platform_users_organization_id_normalized_email",
                schema: "endpoint_platform",
                table: "platform_users",
                columns: new[] { "organization_id", "normalized_email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_platform_users_status",
                schema: "endpoint_platform",
                table: "platform_users",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_role_permissions_permission_id",
                schema: "endpoint_platform",
                table: "role_permissions",
                column: "permission_id");

            migrationBuilder.CreateIndex(
                name: "ix_roles_key_builtin",
                schema: "endpoint_platform",
                table: "roles",
                column: "key",
                unique: true,
                filter: "organization_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_roles_organization_id_key",
                schema: "endpoint_platform",
                table: "roles",
                columns: new[] { "organization_id", "key" },
                unique: true,
                filter: "organization_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log_entries",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "platform_user_roles",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "role_permissions",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "platform_users",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "permissions",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "roles",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "organizations",
                schema: "endpoint_platform");
        }
    }
}
