using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class SoftwareDeployments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "software_deployments",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    package_id = table.Column<Guid>(type: "uuid", nullable: false),
                    package_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    package_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    target_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_display = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_software_deployments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "software_deployment_targets",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    observed_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_software_deployment_targets", x => x.id);
                    table.ForeignKey(
                        name: "fk_software_deployment_targets_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_software_deployment_targets_software_deployments_deployment~",
                        column: x => x.deployment_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "software_deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_software_deployment_targets_device_id",
                schema: "endpoint_platform",
                table: "software_deployment_targets",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_software_deployment_targets_task_id",
                schema: "endpoint_platform",
                table: "software_deployment_targets",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "ux_software_deployment_targets_deployment_device",
                schema: "endpoint_platform",
                table: "software_deployment_targets",
                columns: new[] { "deployment_id", "device_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_software_deployments_org_created",
                schema: "endpoint_platform",
                table: "software_deployments",
                columns: new[] { "organization_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "software_deployment_targets",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "software_deployments",
                schema: "endpoint_platform");
        }
    }
}
