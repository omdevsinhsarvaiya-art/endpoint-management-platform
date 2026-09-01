using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class BitLockerStartupProtectorObservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "has_tpm_pin_protector",
                schema: "endpoint_platform",
                table: "device_bitlocker_volumes",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "has_tpm_protector",
                schema: "endpoint_platform",
                table: "device_bitlocker_volumes",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tpm_pin_protector_ids",
                schema: "endpoint_platform",
                table: "device_bitlocker_volumes",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tpm_protector_ids",
                schema: "endpoint_platform",
                table: "device_bitlocker_volumes",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "has_tpm_pin_protector",
                schema: "endpoint_platform",
                table: "device_bitlocker_volumes");

            migrationBuilder.DropColumn(
                name: "has_tpm_protector",
                schema: "endpoint_platform",
                table: "device_bitlocker_volumes");

            migrationBuilder.DropColumn(
                name: "tpm_pin_protector_ids",
                schema: "endpoint_platform",
                table: "device_bitlocker_volumes");

            migrationBuilder.DropColumn(
                name: "tpm_protector_ids",
                schema: "endpoint_platform",
                table: "device_bitlocker_volumes");
        }
    }
}
