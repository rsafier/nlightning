using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddLiquidityPurchases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LiquidityPurchases",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    FundingTxId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    Role = table.Column<byte>(type: "tinyint", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    RequestedSat = table.Column<long>(type: "bigint", nullable: false),
                    ContributedSat = table.Column<long>(type: "bigint", nullable: false),
                    RateMinAmountSat = table.Column<long>(type: "bigint", nullable: false),
                    RateMaxAmountSat = table.Column<long>(type: "bigint", nullable: false),
                    RateFundingWeight = table.Column<int>(type: "int", nullable: false),
                    RateFeeBasis = table.Column<int>(type: "int", nullable: false),
                    RateFeeBaseSat = table.Column<long>(type: "bigint", nullable: false),
                    RateChannelCreationFeeSat = table.Column<long>(type: "bigint", nullable: false),
                    PaymentType = table.Column<byte>(type: "tinyint", nullable: false),
                    MiningFeeSat = table.Column<long>(type: "bigint", nullable: false),
                    ServiceFeeSat = table.Column<long>(type: "bigint", nullable: false),
                    Signature = table.Column<byte[]>(type: "varbinary(64)", nullable: false),
                    FundingScript = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    PeerNodeId = table.Column<byte[]>(type: "varbinary(33)", nullable: false),
                    LeaseBlocks = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    LeaseStartHeight = table.Column<long>(type: "bigint", nullable: true),
                    ClosedAtHeight = table.Column<long>(type: "bigint", nullable: true),
                    ClosedEarly = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiquidityPurchases", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LiquidityPurchases_ChannelId_FundingTxId",
                table: "LiquidityPurchases",
                columns: new[] { "ChannelId", "FundingTxId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LiquidityPurchases_CreatedAt",
                table: "LiquidityPurchases",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_LiquidityPurchases_Status",
                table: "LiquidityPurchases",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LiquidityPurchases");
        }
    }
}