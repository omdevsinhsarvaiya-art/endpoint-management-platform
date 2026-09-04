using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class DeploymentRetryAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_software_deployment_targets_deployment_device",
                schema: "endpoint_platform",
                table: "software_deployment_targets");

            // Backfilled to 1, not 0: every row that already exists was written by
            // the original deployment, which is attempt one. Zero is not a valid
            // attempt -- the domain refuses it -- and leaving existing rows at 0
            // would make the first retry compute attempt 1 and collide with them.
            migrationBuilder.AddColumn<int>(
                name: "attempt",
                schema: "endpoint_platform",
                table: "software_deployment_targets",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "ux_software_deployment_targets_deployment_device_attempt",
                schema: "endpoint_platform",
                table: "software_deployment_targets",
                columns: new[] { "deployment_id", "device_id", "attempt" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_software_deployment_targets_deployment_device_attempt",
                schema: "endpoint_platform",
                table: "software_deployment_targets");

            migrationBuilder.DropColumn(
                name: "attempt",
                schema: "endpoint_platform",
                table: "software_deployment_targets");

            migrationBuilder.CreateIndex(
                name: "ux_software_deployment_targets_deployment_device",
                schema: "endpoint_platform",
                table: "software_deployment_targets",
                columns: new[] { "deployment_id", "device_id" },
                unique: true);
        }
    }
}
