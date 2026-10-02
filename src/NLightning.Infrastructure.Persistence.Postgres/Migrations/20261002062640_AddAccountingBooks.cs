using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountingBooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accounting_balances",
                columns: table => new
                {
                    account = table.Column<int>(type: "integer", nullable: false),
                    balance_msat = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_balances", x => x.account);
                });

            migrationBuilder.CreateTable(
                name: "accounting_cursor",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    last_ledger_seq = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_cursor", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "accounting_entries",
                columns: table => new
                {
                    ledger_seq = table.Column<long>(type: "bigint", nullable: false),
                    event_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<long>(type: "bigint", nullable: false),
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    payment_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_entries", x => x.ledger_seq);
                });

            migrationBuilder.CreateTable(
                name: "accounting_postings",
                columns: table => new
                {
                    ledger_seq = table.Column<long>(type: "bigint", nullable: false),
                    index = table.Column<int>(type: "integer", nullable: false),
                    account = table.Column<int>(type: "integer", nullable: false),
                    amount_msat = table.Column<long>(type: "bigint", nullable: false),
                    occurred_at = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_postings", x => new { x.ledger_seq, x.index });
                    table.ForeignKey(
                        name: "fk_accounting_postings_accounting_entries_ledger_seq",
                        column: x => x.ledger_seq,
                        principalTable: "accounting_entries",
                        principalColumn: "ledger_seq",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_entries_event_key",
                table: "accounting_entries",
                column: "event_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_accounting_entries_occurred_at",
                table: "accounting_entries",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_accounting_postings_account_occurred_at",
                table: "accounting_postings",
                columns: new[] { "account", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "accounting_balances");

            migrationBuilder.DropTable(
                name: "accounting_cursor");

            migrationBuilder.DropTable(
                name: "accounting_postings");

            migrationBuilder.DropTable(
                name: "accounting_entries");
        }
    }
}
