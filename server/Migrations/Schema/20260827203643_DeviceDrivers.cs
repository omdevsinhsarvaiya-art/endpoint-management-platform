using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class DeviceDrivers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_drivers",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    device_name = table.Column<string>(type: "character varying(384)", maxLength: 384, nullable: false),
                    device_class = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    manufacturer = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    driver_provider = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    driver_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    driver_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    inf_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    problem_code = table.Column<int>(type: "integer", nullable: true),
                    is_signed = table.Column<bool>(type: "boolean", nullable: true),
                    collected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_drivers", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_drivers_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_drivers_device_id",
                schema: "endpoint_platform",
                table: "device_drivers",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_drivers_problem_code",
                schema: "endpoint_platform",
                table: "device_drivers",
                column: "problem_code",
                filter: "problem_code IS NOT NULL AND problem_code <> 0");

            migrationBuilder.CreateIndex(
                name: "ix_device_drivers_provider_version",
                schema: "endpoint_platform",
                table: "device_drivers",
                columns: new[] { "driver_provider", "driver_version" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_drivers",
                schema: "endpoint_platform");
        }
    }
}
