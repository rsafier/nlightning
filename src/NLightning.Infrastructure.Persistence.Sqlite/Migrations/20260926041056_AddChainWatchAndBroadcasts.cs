using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
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
                    Height = table.Column<uint>(type: "INTEGER", nullable: false),
                    BlockHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PreviousBlockHash = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockHeaders", x => x.Height);
                });

            migrationBuilder.CreateTable(
                name: "BroadcastTransactions",
                columns: table => new
                {
                    TransactionId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    RawTransaction = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Purpose = table.Column<byte>(type: "INTEGER", nullable: false),
                    FeeratePerKw = table.Column<long>(type: "INTEGER", nullable: false),
                    ReplacesTransactionId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    FirstBroadcastHeight = table.Column<uint>(type: "INTEGER", nullable: false),
                    State = table.Column<byte>(type: "INTEGER", nullable: false),
                    ConfirmedHeight = table.Column<uint>(type: "INTEGER", nullable: true),
                    ConfirmedBlockHash = table.Column<byte[]>(type: "BLOB", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BroadcastTransactions", x => x.TransactionId);
                });

            migrationBuilder.CreateTable(
                name: "WatchedOutpoints",
                columns: table => new
                {
                    TransactionId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    OutputIndex = table.Column<uint>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Purpose = table.Column<byte>(type: "INTEGER", nullable: false),
                    SpentByTransactionId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    SpentAtHeight = table.Column<uint>(type: "INTEGER", nullable: true),
                    SpentBlockHash = table.Column<byte[]>(type: "BLOB", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
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
                "INSERT INTO \"WatchedOutpoints\" (\"TransactionId\", \"OutputIndex\", \"ChannelId\", \"Purpose\", \"CreatedAt\") SELECT c.\"FundingTxId\", c.\"FundingOutputIndex\", c.\"ChannelId\", 1, CAST(strftime('%s', 'now') AS INTEGER) * 10000000 + 621355968000000000 FROM \"Channels\" c WHERE c.\"State\" >= 2 AND c.\"State\" NOT IN (40, 50) AND NOT EXISTS (SELECT 1 FROM \"WatchedOutpoints\" o WHERE o.\"TransactionId\" = c.\"FundingTxId\" AND o.\"OutputIndex\" = c.\"FundingOutputIndex\");");
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