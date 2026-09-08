using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class ApplicationDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "category",
                schema: "endpoint_platform",
                table: "device_software",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "confidence",
                schema: "endpoint_platform",
                table: "device_software",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "executable_path",
                schema: "endpoint_platform",
                table: "device_software",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "identity_kind",
                schema: "endpoint_platform",
                table: "device_software",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "package_family_name",
                schema: "endpoint_platform",
                table: "device_software",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "package_full_name",
                schema: "endpoint_platform",
                table: "device_software",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "signature_status",
                schema: "endpoint_platform",
                table: "device_software",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "signer_subject",
                schema: "endpoint_platform",
                table: "device_software",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stable_key",
                schema: "endpoint_platform",
                table: "device_software",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "upgrade_code",
                schema: "endpoint_platform",
                table: "device_software",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "version_key",
                schema: "endpoint_platform",
                table: "device_software",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "device_software_evidence",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_software_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(384)", maxLength: 384, nullable: true),
                    detail = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_software_evidence", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_software_evidence_device_software_device_software_id",
                        column: x => x.device_software_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "device_software",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_software_category",
                schema: "endpoint_platform",
                table: "device_software",
                column: "category");

            migrationBuilder.CreateIndex(
                name: "ix_device_software_stable_key",
                schema: "endpoint_platform",
                table: "device_software",
                column: "stable_key");

            migrationBuilder.CreateIndex(
                name: "ix_device_software_evidence_device_software_id",
                schema: "endpoint_platform",
                table: "device_software_evidence",
                column: "device_software_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_software_evidence",
                schema: "endpoint_platform");

            migrationBuilder.DropIndex(
                name: "ix_device_software_category",
                schema: "endpoint_platform",
                table: "device_software");

            migrationBuilder.DropIndex(
                name: "ix_device_software_stable_key",
                schema: "endpoint_platform",
                table: "device_software");

            migrationBuilder.DropColumn(
                name: "category",
                schema: "endpoint_platform",
                table: "device_software");

            migrationBuilder.DropColumn(
                name: "confidence",
                schema: "endpoint_platform",
                table: "device_software");

            migrationBuilder.DropColumn(
                name: "executable_path",
                schema: "endpoint_platform",
                table: "device_software");

            migrationBuilder.DropColumn(
                name: "identity_kind",
                schema: "endpoint_platform",
                table: "device_software");

            migrationBuilder.DropColumn(
                name: "package_family_name",
                schema: "endpoint_platform",
                table: "device_software");

            migrationBuilder.DropColumn(
                name: "package_full_name",
                schema: "endpoint_platform",
                table: "device_software");

            migrationBuilder.DropColumn(
                name: "signature_status",
                schema: "endpoint_platform",
                table: "device_software");

            migrationBuilder.DropColumn(
                name: "signer_subject",
                schema: "endpoint_platform",
                table: "device_software");

            migrationBuilder.DropColumn(
                name: "stable_key",
                schema: "endpoint_platform",
                table: "device_software");

            migrationBuilder.DropColumn(
                name: "upgrade_code",
                schema: "endpoint_platform",
                table: "device_software");

            migrationBuilder.DropColumn(
                name: "version_key",
                schema: "endpoint_platform",
                table: "device_software");
        }
    }
}
