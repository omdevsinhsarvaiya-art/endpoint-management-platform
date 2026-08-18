using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class DeviceSoftware : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_software",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(384)", maxLength: 384, nullable: false),
                    version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    publisher = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    install_date = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    install_location = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    architecture = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    collected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_software", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_software_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_software_device_id",
                schema: "endpoint_platform",
                table: "device_software",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_software_name_version",
                schema: "endpoint_platform",
                table: "device_software",
                columns: new[] { "name", "version" });

            migrationBuilder.CreateIndex(
                name: "ix_device_software_publisher",
                schema: "endpoint_platform",
                table: "device_software",
                column: "publisher");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_software",
                schema: "endpoint_platform");
        }
    }
}
