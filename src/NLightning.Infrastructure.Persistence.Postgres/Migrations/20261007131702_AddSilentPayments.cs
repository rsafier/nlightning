using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSilentPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "is_address_change",
                table: "utxos",
                type: "boolean",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.AlterColumn<byte>(
                name: "address_type",
                table: "utxos",
                type: "smallint",
                nullable: true,
                oldClrType: typeof(byte),
                oldType: "smallint");

            migrationBuilder.AlterColumn<long>(
                name: "address_index",
                table: "utxos",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<long>(
                name: "silent_payment_index",
                table: "utxos",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "silent_payment_transaction_id",
                table: "utxos",
                type: "bytea",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "silent_payment_labels",
                columns: table => new
                {
                    m = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at_height = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_silent_payment_labels", x => x.m);
                });

            migrationBuilder.CreateTable(
                name: "silent_payment_outputs",
                columns: table => new
                {
                    transaction_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    index = table.Column<long>(type: "bigint", nullable: false),
                    output_key = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    tweak = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    label = table.Column<long>(type: "bigint", nullable: true),
                    amount_sats = table.Column<long>(type: "bigint", nullable: false),
                    block_height = table.Column<long>(type: "bigint", nullable: false),
                    block_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    spent_by_transaction_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    spent_at_height = table.Column<long>(type: "bigint", nullable: true),
                    ignored = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_silent_payment_outputs", x => new { x.transaction_id, x.index });
                });

            migrationBuilder.CreateTable(
                name: "silent_payment_scan_state",
                columns: table => new
                {
                    id = table.Column<byte>(type: "smallint", nullable: false),
                    birthday_height = table.Column<long>(type: "bigint", nullable: false),
                    live_from_height = table.Column<long>(type: "bigint", nullable: false),
                    rescan_cursor_height = table.Column<long>(type: "bigint", nullable: true),
                    rescan_cursor_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    rescan_target_height = table.Column<long>(type: "bigint", nullable: true),
                    recovery_label_count = table.Column<long>(type: "bigint", nullable: false),
                    live_cursor_height = table.Column<long>(type: "bigint", nullable: true),
                    live_cursor_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    prevout_source = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_silent_payment_scan_state", x => x.id);
                    table.CheckConstraint("CK_SilentPaymentScanState_Singleton", "id = 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_utxos_silent_payment_transaction_id_silent_payment_index",
                table: "utxos",
                columns: new[] { "silent_payment_transaction_id", "silent_payment_index" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Utxos_Ownership",
                table: "utxos",
                sql: "((address_index IS NOT NULL AND is_address_change IS NOT NULL AND address_type IS NOT NULL AND silent_payment_transaction_id IS NULL AND silent_payment_index IS NULL) OR (address_index IS NULL AND is_address_change IS NULL AND address_type IS NULL AND silent_payment_transaction_id IS NOT NULL AND silent_payment_index IS NOT NULL AND silent_payment_transaction_id = transaction_id AND silent_payment_index = index))");

            migrationBuilder.CreateIndex(
                name: "ix_silent_payment_labels_name",
                table: "silent_payment_labels",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_silent_payment_outputs_block_height",
                table: "silent_payment_outputs",
                column: "block_height");

            migrationBuilder.CreateIndex(
                name: "ix_silent_payment_outputs_spent_by_transaction_id",
                table: "silent_payment_outputs",
                column: "spent_by_transaction_id");

            migrationBuilder.AddForeignKey(
                name: "fk_utxos_silent_payment_outputs_silent_payment_transaction_id_",
                table: "utxos",
                columns: new[] { "silent_payment_transaction_id", "silent_payment_index" },
                principalTable: "silent_payment_outputs",
                principalColumns: new[] { "transaction_id", "index" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_utxos_silent_payment_outputs_silent_payment_transaction_id_",
                table: "utxos");

            migrationBuilder.DropTable(
                name: "silent_payment_labels");

            migrationBuilder.DropTable(
                name: "silent_payment_outputs");

            migrationBuilder.DropTable(
                name: "silent_payment_scan_state");

            migrationBuilder.DropIndex(
                name: "ix_utxos_silent_payment_transaction_id_silent_payment_index",
                table: "utxos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Utxos_Ownership",
                table: "utxos");

            migrationBuilder.DropColumn(
                name: "silent_payment_index",
                table: "utxos");

            migrationBuilder.DropColumn(
                name: "silent_payment_transaction_id",
                table: "utxos");

            migrationBuilder.AlterColumn<bool>(
                name: "is_address_change",
                table: "utxos",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: true);

            migrationBuilder.AlterColumn<byte>(
                name: "address_type",
                table: "utxos",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0,
                oldClrType: typeof(byte),
                oldType: "smallint",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "address_index",
                table: "utxos",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);
        }
    }
}