using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountingBooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountingBalances",
                columns: table => new
                {
                    Account = table.Column<int>(type: "INTEGER", nullable: false),
                    BalanceMsat = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingBalances", x => x.Account);
                });

            migrationBuilder.CreateTable(
                name: "AccountingCursor",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    LastLedgerSeq = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingCursor", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountingEntries",
                columns: table => new
                {
                    LedgerSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    EventKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    PaymentHash = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingEntries", x => x.LedgerSeq);
                });

            migrationBuilder.CreateTable(
                name: "AccountingPostings",
                columns: table => new
                {
                    LedgerSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    Account = table.Column<int>(type: "INTEGER", nullable: false),
                    AmountMsat = table.Column<long>(type: "INTEGER", nullable: false),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingPostings", x => new { x.LedgerSeq, x.Index });
                    table.ForeignKey(
                        name: "FK_AccountingPostings_AccountingEntries_LedgerSeq",
                        column: x => x.LedgerSeq,
                        principalTable: "AccountingEntries",
                        principalColumn: "LedgerSeq",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEntries_EventKey",
                table: "AccountingEntries",
                column: "EventKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEntries_OccurredAt",
                table: "AccountingEntries",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingPostings_Account_OccurredAt",
                table: "AccountingPostings",
                columns: new[] { "Account", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountingBalances");

            migrationBuilder.DropTable(
                name: "AccountingCursor");

            migrationBuilder.DropTable(
                name: "AccountingPostings");

            migrationBuilder.DropTable(
                name: "AccountingEntries");
        }
    }
}
