using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class DeviceInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "inventory_collected_at",
                schema: "endpoint_platform",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "inventory_requested_at",
                schema: "endpoint_platform",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "logged_on_user",
                schema: "endpoint_platform",
                table: "devices",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "device_hardware",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    serial_number = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    manufacturer = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    cpu_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    cpu_physical_cores = table.Column<int>(type: "integer", nullable: true),
                    cpu_logical_processors = table.Column<int>(type: "integer", nullable: true),
                    total_memory_bytes = table.Column<long>(type: "bigint", nullable: true),
                    disks_json = table.Column<string>(type: "jsonb", nullable: true),
                    collected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_hardware", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_hardware_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_network_interfaces",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    mac_address = table.Column<string>(type: "character varying(23)", maxLength: 23, nullable: true),
                    ip_addresses_json = table.Column<string>(type: "jsonb", nullable: true),
                    is_up = table.Column<bool>(type: "boolean", nullable: false),
                    collected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_network_interfaces", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_network_interfaces_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_hardware_device_id",
                schema: "endpoint_platform",
                table: "device_hardware",
                column: "device_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_hardware_serial_number",
                schema: "endpoint_platform",
                table: "device_hardware",
                column: "serial_number");

            migrationBuilder.CreateIndex(
                name: "ix_device_network_interfaces_device_id",
                schema: "endpoint_platform",
                table: "device_network_interfaces",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_network_interfaces_mac_address",
                schema: "endpoint_platform",
                table: "device_network_interfaces",
                column: "mac_address");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_hardware",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "device_network_interfaces",
                schema: "endpoint_platform");

            migrationBuilder.DropColumn(
                name: "inventory_collected_at",
                schema: "endpoint_platform",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "inventory_requested_at",
                schema: "endpoint_platform",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "logged_on_user",
                schema: "endpoint_platform",
                table: "devices");
        }
    }
}
