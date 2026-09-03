using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class SoftwareInventoryScopeAndProductCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "installation_scope",
                schema: "endpoint_platform",
                table: "device_software",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "installed_for_user",
                schema: "endpoint_platform",
                table: "device_software",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "product_code",
                schema: "endpoint_platform",
                table: "device_software",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_software_product_code",
                schema: "endpoint_platform",
                table: "device_software",
                column: "product_code");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_device_software_product_code",
                schema: "endpoint_platform",
                table: "device_software");

            migrationBuilder.DropColumn(
                name: "installation_scope",
                schema: "endpoint_platform",
                table: "device_software");

            migrationBuilder.DropColumn(
                name: "installed_for_user",
                schema: "endpoint_platform",
                table: "device_software");

            migrationBuilder.DropColumn(
                name: "product_code",
                schema: "endpoint_platform",
                table: "device_software");
        }
    }
}
