using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class UsbPeripheralControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "usb_access_requests",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    usb_device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    justification = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_by_display = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    revoked_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usb_access_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_usb_access_requests_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "usb_devices",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    device_class = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    vendor_id = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    product_id = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    serial_number = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    manufacturer = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    product = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    hardware_ids = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    is_connected = table.Column<bool>(type: "boolean", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disconnected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    policy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    policy_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    enforced_policy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    enforced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    enforcement_error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usb_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_usb_devices_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_usb_access_requests_device_status",
                schema: "endpoint_platform",
                table: "usb_access_requests",
                columns: new[] { "device_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_usb_access_requests_status_expires",
                schema: "endpoint_platform",
                table: "usb_access_requests",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_usb_devices_device_instance",
                schema: "endpoint_platform",
                table: "usb_devices",
                columns: new[] { "device_id", "instance_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_usb_devices_organization_policy",
                schema: "endpoint_platform",
                table: "usb_devices",
                columns: new[] { "organization_id", "policy" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "usb_access_requests",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "usb_devices",
                schema: "endpoint_platform");
        }
    }
}
