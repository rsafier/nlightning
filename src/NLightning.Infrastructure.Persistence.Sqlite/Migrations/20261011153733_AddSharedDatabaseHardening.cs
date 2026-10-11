using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedDatabaseHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- Hand-written guard (NL-1390): a ledger sequence held twice (two sealers forked the chain) would fail the
            // unique index below with a bare constraint error; refuse with the repair procedure instead (SQLite has no
            // RAISE outside triggers: a temp table's named CHECK carries the message) ----
            migrationBuilder.Sql("DROP TABLE IF EXISTS temp.\"AddSharedDatabaseHardeningGuard\";");
            migrationBuilder.Sql("CREATE TEMP TABLE \"AddSharedDatabaseHardeningGuard\" (\"ForkedSequences\" INTEGER NOT NULL CONSTRAINT \"AddSharedDatabaseHardening_refused_AccountingEvents_holds_a_ledger_sequence_twice_see_ACCOUNTING_PLAN_Forked_ledger_repair_NL_1390\" CHECK (\"ForkedSequences\" = 0));");
            migrationBuilder.Sql("INSERT INTO \"AddSharedDatabaseHardeningGuard\" (\"ForkedSequences\") SELECT COUNT(*) FROM (SELECT \"LedgerSeq\" FROM \"AccountingEvents\" WHERE \"LedgerSeq\" IS NOT NULL GROUP BY \"LedgerSeq\" HAVING COUNT(*) > 1);");
            migrationBuilder.Sql("DROP TABLE temp.\"AddSharedDatabaseHardeningGuard\";");
            // ---- End of the hand-written guard ----

            migrationBuilder.DropIndex(
                name: "IX_AccountingEvents_LedgerSeq",
                table: "AccountingEvents");

            migrationBuilder.CreateTable(
                name: "ChannelKeyIndexHighWater",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    HighWater = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelKeyIndexHighWater", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MacaroonRootKeys",
                columns: table => new
                {
                    RootKeyId = table.Column<long>(type: "INTEGER", nullable: false),
                    Nonce = table.Column<byte[]>(type: "BLOB", maxLength: 24, nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "BLOB", maxLength: 48, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MacaroonRootKeys", x => x.RootKeyId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEvents_LedgerSeq",
                table: "AccountingEvents",
                column: "LedgerSeq",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelKeyIndexHighWater");

            migrationBuilder.DropTable(
                name: "MacaroonRootKeys");

            migrationBuilder.DropIndex(
                name: "IX_AccountingEvents_LedgerSeq",
                table: "AccountingEvents");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEvents_LedgerSeq",
                table: "AccountingEvents",
                column: "LedgerSeq");
        }
    }
}