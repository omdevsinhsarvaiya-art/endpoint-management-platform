using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class AutomaticBitLockerEscrow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "escrowed_by_user_id",
                schema: "endpoint_platform",
                table: "bitlocker_recovery_escrows",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "origin",
                schema: "endpoint_platform",
                table: "bitlocker_recovery_escrows",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                // Every row that exists when this runs was typed in by an
                // administrator under the manual model. Backfilling anything else
                // would misattribute it.
                defaultValue: "Manual");

            migrationBuilder.AddColumn<string>(
                name: "seal_scheme",
                schema: "endpoint_platform",
                table: "bitlocker_recovery_escrows",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                // ...and every one of them was sealed with the Admin API's
                // symmetric master key. The reveal path dispatches on this, so an
                // empty value here would make existing keys unreadable.
                defaultValue: "aesgcm-v1");

            migrationBuilder.AddColumn<string>(
                name: "sealing_key_fingerprint",
                schema: "endpoint_platform",
                table: "agent_credentials",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "bitlocker_escrow_attempts",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    volume_device_identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    key_protector_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_failure = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    escrowed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reset_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reset_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bitlocker_escrow_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_bitlocker_escrow_attempts_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bitlocker_escrow_attempts_due",
                schema: "endpoint_platform",
                table: "bitlocker_escrow_attempts",
                columns: new[] { "state", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ux_bitlocker_escrow_attempts_protector",
                schema: "endpoint_platform",
                table: "bitlocker_escrow_attempts",
                columns: new[] { "device_id", "volume_device_identifier", "key_protector_id" },
                unique: true);
        }

        /// <inheritdoc />
        /// <summary>
        /// Reverses this migration, but only while it is still safe to do so.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This migration is irreversible once an automatic escrow row exists,
        /// and it says so rather than pretending otherwise.</b> Two columns it adds
        /// are load-bearing for those rows. <c>escrowed_by_user_id</c> is null for
        /// them, because no administrator filed them, so restoring NOT NULL cannot
        /// succeed without inventing an actor. More seriously,
        /// <c>seal_scheme</c> is what tells the reveal path that a row was sealed
        /// with the hybrid endpoint scheme rather than the symmetric one -- drop it
        /// and the ciphertext is still there but nothing knows how to open it. The
        /// row would look intact and be unrecoverable, which is the worst of the
        /// available failures.
        /// </para>
        /// <para>
        /// Deleting those rows to make the rollback succeed was rejected outright:
        /// a rollback that destroys recovery credentials is not a rollback, and the
        /// keys it would destroy are the ones nobody notices are missing until a
        /// disk will not unlock.
        /// </para>
        /// <para>
        /// So the guard refuses instead. With no automatic rows present -- the
        /// normal case for a rollback shortly after deployment -- nothing is at
        /// risk and the reversal proceeds exactly as it would have. With any
        /// present, the migration aborts in a transaction that rolls back cleanly,
        /// leaving the schema untouched and naming what must be done first. Going
        /// back from there is a deliberate data decision for an operator to make,
        /// not something a schema migration should take on their behalf.
        /// </para>
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Runs before any DDL. EF wraps a migration in a transaction, so
            // raising here leaves the database exactly as it was found.
            migrationBuilder.Sql("""
                DO $$
                DECLARE automatic_rows bigint;
                BEGIN
                    SELECT count(*) INTO automatic_rows
                    FROM endpoint_platform.bitlocker_recovery_escrows
                    WHERE origin = 'Automatic' OR seal_scheme = 'hybrid-rsa-v1';

                    IF automatic_rows > 0 THEN
                        RAISE EXCEPTION
                            'Cannot roll back AutomaticBitLockerEscrow: % automatically escrowed '
                            'recovery password(s) exist. Reverting would drop seal_scheme, leaving '
                            'their ciphertext undecryptable, and restore a NOT NULL constraint they '
                            'cannot satisfy. Reveal or export the affected keys and remove those '
                            'rows deliberately before rolling back.',
                            automatic_rows
                        USING ERRCODE = 'raise_exception';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropTable(
                name: "bitlocker_escrow_attempts",
                schema: "endpoint_platform");

            migrationBuilder.DropColumn(
                name: "origin",
                schema: "endpoint_platform",
                table: "bitlocker_recovery_escrows");

            migrationBuilder.DropColumn(
                name: "seal_scheme",
                schema: "endpoint_platform",
                table: "bitlocker_recovery_escrows");

            migrationBuilder.DropColumn(
                name: "sealing_key_fingerprint",
                schema: "endpoint_platform",
                table: "agent_credentials");

            // No defaultValue. The guard above guarantees every surviving row has a
            // real administrator id, so a fallback would never be used -- and an
            // empty-GUID default would quietly manufacture a fictional actor if the
            // guard were ever weakened. Without one, an unexpected null fails the
            // ALTER loudly instead.
            migrationBuilder.AlterColumn<Guid>(
                name: "escrowed_by_user_id",
                schema: "endpoint_platform",
                table: "bitlocker_recovery_escrows",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
