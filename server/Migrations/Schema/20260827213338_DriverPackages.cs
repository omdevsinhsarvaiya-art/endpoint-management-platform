using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class DriverPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "driver_packages",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    file_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    inf_file_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    hardware_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    driver_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    required_signer_subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_display = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    is_withdrawn = table.Column<bool>(type: "boolean", nullable: false),
                    withdrawn_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_driver_packages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_driver_packages_hardware_id",
                schema: "endpoint_platform",
                table: "driver_packages",
                column: "hardware_id");

            migrationBuilder.CreateIndex(
                name: "ux_driver_packages_organization_sha256",
                schema: "endpoint_platform",
                table: "driver_packages",
                columns: new[] { "organization_id", "sha256" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "driver_packages",
                schema: "endpoint_platform");
        }
    }
}
