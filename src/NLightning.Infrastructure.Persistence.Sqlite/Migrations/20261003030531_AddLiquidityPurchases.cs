using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
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
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    FundingTxId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Role = table.Column<byte>(type: "INTEGER", nullable: false),
                    Kind = table.Column<byte>(type: "INTEGER", nullable: false),
                    RequestedSat = table.Column<long>(type: "INTEGER", nullable: false),
                    ContributedSat = table.Column<long>(type: "INTEGER", nullable: false),
                    RateMinAmountSat = table.Column<uint>(type: "INTEGER", nullable: false),
                    RateMaxAmountSat = table.Column<uint>(type: "INTEGER", nullable: false),
                    RateFundingWeight = table.Column<ushort>(type: "INTEGER", nullable: false),
                    RateFeeBasis = table.Column<ushort>(type: "INTEGER", nullable: false),
                    RateFeeBaseSat = table.Column<uint>(type: "INTEGER", nullable: false),
                    RateChannelCreationFeeSat = table.Column<uint>(type: "INTEGER", nullable: false),
                    PaymentType = table.Column<byte>(type: "INTEGER", nullable: false),
                    MiningFeeSat = table.Column<long>(type: "INTEGER", nullable: false),
                    ServiceFeeSat = table.Column<long>(type: "INTEGER", nullable: false),
                    Signature = table.Column<byte[]>(type: "BLOB", nullable: false),
                    FundingScript = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PeerNodeId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    LeaseBlocks = table.Column<uint>(type: "INTEGER", nullable: false),
                    Status = table.Column<byte>(type: "INTEGER", nullable: false),
                    LeaseStartHeight = table.Column<uint>(type: "INTEGER", nullable: true),
                    ClosedAtHeight = table.Column<uint>(type: "INTEGER", nullable: true),
                    ClosedEarly = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
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