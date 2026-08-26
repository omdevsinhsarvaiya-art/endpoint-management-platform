using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class UsbEnabledPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfilled to ReadOnly rather than the empty string EF defaults to.
            // Read-only was the only level the platform could issue before this
            // migration, so it is the truth for every existing row — and an empty
            // string would fail to parse back into the enum, breaking the policy
            // build for any endpoint with grant history.
            migrationBuilder.AddColumn<string>(
                name: "granted_policy",
                schema: "endpoint_platform",
                table: "usb_access_requests",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "ReadOnly");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "granted_policy",
                schema: "endpoint_platform",
                table: "usb_access_requests");
        }
    }
}
