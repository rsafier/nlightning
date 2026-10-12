using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
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
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            // Backfill the derived query reference without changing sealed event payloads or hashes.
            migrationBuilder.Sql("""
                UPDATE [AccountingEvents] SET [ReversesEventKey] = COALESCE(
                    NULLIF(JSON_VALUE(CASE WHEN ISJSON([Details]) = 1 THEN [Details] ELSE '{}' END, '$.reverses'), ''),
                    CASE WHEN CHARINDEX(':ver:', REVERSE([EventKey])) > 0
                         THEN NULLIF(LEFT([EventKey], LEN([EventKey]) - CHARINDEX(':ver:', REVERSE([EventKey])) - 4), '') END
                ) WHERE [Kind] = 60;
                """);

            migrationBuilder.CreateTable(
                name: "OnchainHtlcObservations",
                columns: table => new
                {
                    ChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    Direction = table.Column<byte>(type: "tinyint", nullable: false),
                    HtlcId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    Settled = table.Column<bool>(type: "bit", nullable: false),
                    ObservedAt = table.Column<long>(type: "bigint", nullable: false)
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