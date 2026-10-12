using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddChainWatchAndBroadcasts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BlockHeaders",
                columns: table => new
                {
                    Height = table.Column<long>(type: "bigint", nullable: false),
                    BlockHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    PreviousBlockHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockHeaders", x => x.Height);
                });

            migrationBuilder.CreateTable(
                name: "BroadcastTransactions",
                columns: table => new
                {
                    TransactionId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    ChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    RawTransaction = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Purpose = table.Column<byte>(type: "tinyint", nullable: false),
                    FeeratePerKw = table.Column<long>(type: "bigint", nullable: false),
                    ReplacesTransactionId = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    FirstBroadcastHeight = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<byte>(type: "tinyint", nullable: false),
                    ConfirmedHeight = table.Column<long>(type: "bigint", nullable: true),
                    ConfirmedBlockHash = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BroadcastTransactions", x => x.TransactionId);
                });

            migrationBuilder.CreateTable(
                name: "WatchedOutpoints",
                columns: table => new
                {
                    TransactionId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    OutputIndex = table.Column<long>(type: "bigint", nullable: false),
                    ChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    Purpose = table.Column<byte>(type: "tinyint", nullable: false),
                    SpentByTransactionId = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    SpentAtHeight = table.Column<long>(type: "bigint", nullable: true),
                    SpentBlockHash = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchedOutpoints", x => new { x.TransactionId, x.OutputIndex });
                });

            migrationBuilder.CreateIndex(
                name: "IX_BroadcastTransactions_ChannelId",
                table: "BroadcastTransactions",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_BroadcastTransactions_ConfirmedHeight",
                table: "BroadcastTransactions",
                column: "ConfirmedHeight");

            migrationBuilder.CreateIndex(
                name: "IX_BroadcastTransactions_State",
                table: "BroadcastTransactions",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_WatchedOutpoints_ChannelId",
                table: "WatchedOutpoints",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_WatchedOutpoints_SpentAtHeight",
                table: "WatchedOutpoints",
                column: "SpentAtHeight");

            // ---- Hand-written data step (not generated) ----
            // BOLT 5 plan O0-T4: every channel past funding_created that is not Closed (40) or Stale (50) gets its
            // funding output watched (purpose 1 = FundingOutput); CreatedAt is the migration time in UTC ticks. The
            // monitor runs the same backfill at every start, for channels stored later without a watch.
            migrationBuilder.Sql(
                "INSERT INTO [WatchedOutpoints] ([TransactionId], [OutputIndex], [ChannelId], [Purpose], [CreatedAt]) SELECT c.[FundingTxId], c.[FundingOutputIndex], c.[ChannelId], 1, CAST(DATEDIFF_BIG(SECOND, '1970-01-01', SYSUTCDATETIME()) AS bigint) * 10000000 + 621355968000000000 FROM [Channels] c WHERE c.[State] >= 2 AND c.[State] NOT IN (40, 50) AND NOT EXISTS (SELECT 1 FROM [WatchedOutpoints] o WHERE o.[TransactionId] = c.[FundingTxId] AND o.[OutputIndex] = c.[FundingOutputIndex]);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlockHeaders");

            migrationBuilder.DropTable(
                name: "BroadcastTransactions");

            migrationBuilder.DropTable(
                name: "WatchedOutpoints");
        }
    }
}