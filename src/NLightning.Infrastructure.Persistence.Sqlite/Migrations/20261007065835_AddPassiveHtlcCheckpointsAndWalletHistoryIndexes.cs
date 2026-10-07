using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddPassiveHtlcCheckpointsAndWalletHistoryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReversesEventKey",
                table: "AccountingEvents",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            // Backfill the derived query reference without changing sealed event payloads or hashes.
            migrationBuilder.Sql("""
                WITH RECURSIVE positions(Id, EventKey, Position, NextPosition) AS (
                    SELECT Id, EventKey, 0, instr(EventKey, ':rev:') FROM AccountingEvents WHERE Kind = 60
                    UNION ALL
                    SELECT Id, EventKey, NextPosition,
                           CASE WHEN instr(substr(EventKey, NextPosition + 5), ':rev:') > 0
                                THEN NextPosition + 4 + instr(substr(EventKey, NextPosition + 5), ':rev:') ELSE 0 END
                    FROM positions WHERE NextPosition > 0
                )
                UPDATE AccountingEvents SET ReversesEventKey = COALESCE(
                    NULLIF(CASE WHEN json_valid(Details) THEN json_extract(Details, '$.reverses') END, ''),
                    NULLIF(substr(EventKey, 1, (SELECT MAX(Position) FROM positions WHERE positions.Id = AccountingEvents.Id) - 1), '')
                ) WHERE Kind = 60;
                """);

            migrationBuilder.CreateTable(
                name: "OnchainHtlcObservations",
                columns: table => new
                {
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Direction = table.Column<byte>(type: "INTEGER", nullable: false),
                    HtlcId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Settled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ObservedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnchainHtlcObservations", x => new { x.ChannelId, x.Direction, x.HtlcId, x.Settled });
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEvents_Kind_BlockHeight_LedgerSeq",
                table: "AccountingEvents",
                columns: new[] { "Kind", "BlockHeight", "LedgerSeq" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEvents_ReversesEventKey",
                table: "AccountingEvents",
                column: "ReversesEventKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OnchainHtlcObservations");

            migrationBuilder.DropIndex(
                name: "IX_AccountingEvents_Kind_BlockHeight_LedgerSeq",
                table: "AccountingEvents");

            migrationBuilder.DropIndex(
                name: "IX_AccountingEvents_ReversesEventKey",
                table: "AccountingEvents");

            migrationBuilder.DropColumn(
                name: "ReversesEventKey",
                table: "AccountingEvents");
        }
    }
}