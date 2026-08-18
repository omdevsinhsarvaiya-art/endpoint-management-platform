using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class PolicyEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "policies",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    current_version_number = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policies", x => x.id);
                    table.ForeignKey(
                        name: "fk_policies_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "policy_assignments",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policy_assignments", x => x.id);
                    table.ForeignKey(
                        name: "fk_policy_assignments_policies_policy_id",
                        column: x => x.policy_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "policies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "policy_compliance_results",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version_number = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    deviations_json = table.Column<string>(type: "jsonb", nullable: true),
                    evaluated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policy_compliance_results", x => x.id);
                    table.ForeignKey(
                        name: "fk_policy_compliance_results_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_policy_compliance_results_policies_policy_id",
                        column: x => x.policy_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "policies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "policy_versions",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    desired_state_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policy_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_policy_versions_policies_policy_id",
                        column: x => x.policy_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "policies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_policies_organization_id",
                schema: "endpoint_platform",
                table: "policies",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_policy_assignments_policy_target",
                schema: "endpoint_platform",
                table: "policy_assignments",
                columns: new[] { "policy_id", "target_type", "target_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_policy_assignments_target",
                schema: "endpoint_platform",
                table: "policy_assignments",
                columns: new[] { "target_type", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ix_policy_compliance_device_policy",
                schema: "endpoint_platform",
                table: "policy_compliance_results",
                columns: new[] { "device_id", "policy_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_policy_compliance_policy_state",
                schema: "endpoint_platform",
                table: "policy_compliance_results",
                columns: new[] { "policy_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_policy_versions_policy_id_version_number",
                schema: "endpoint_platform",
                table: "policy_versions",
                columns: new[] { "policy_id", "version_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "policy_assignments",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "policy_compliance_results",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "policy_versions",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "policies",
                schema: "endpoint_platform");
        }
    }
}
