using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedDatabaseHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- Hand-written guard (NL-1390): a ledger sequence held twice (two sealers forked the chain) would fail the
            // unique index below with a bare constraint error; refuse with the repair procedure instead ----
            migrationBuilder.Sql("DO $$ BEGIN IF EXISTS (SELECT 1 FROM accounting_events WHERE ledger_seq IS NOT NULL GROUP BY ledger_seq HAVING COUNT(*) > 1) THEN RAISE EXCEPTION 'AddSharedDatabaseHardening refused: AccountingEvents holds a ledger sequence twice (two sealers forked the hash chain); repair it first, see docs/agents/ACCOUNTING_PLAN.md section Forked ledger repair (NL-1390)'; END IF; END $$;");
            // ---- End of the hand-written guard ----

            migrationBuilder.DropIndex(
                name: "ix_accounting_events_ledger_seq",
                table: "accounting_events");

            migrationBuilder.CreateTable(
                name: "channel_key_index_high_water",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    high_water = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_key_index_high_water", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "macaroon_root_keys",
                columns: table => new
                {
                    root_key_id = table.Column<long>(type: "bigint", nullable: false),
                    nonce = table.Column<byte[]>(type: "bytea", maxLength: 24, nullable: false),
                    ciphertext = table.Column<byte[]>(type: "bytea", maxLength: 48, nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_macaroon_root_keys", x => x.root_key_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_events_ledger_seq",
                table: "accounting_events",
                column: "ledger_seq",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_key_index_high_water");

            migrationBuilder.DropTable(
                name: "macaroon_root_keys");

            migrationBuilder.DropIndex(
                name: "ix_accounting_events_ledger_seq",
                table: "accounting_events");

            migrationBuilder.CreateIndex(
                name: "ix_accounting_events_ledger_seq",
                table: "accounting_events",
                column: "ledger_seq");
        }
    }
}