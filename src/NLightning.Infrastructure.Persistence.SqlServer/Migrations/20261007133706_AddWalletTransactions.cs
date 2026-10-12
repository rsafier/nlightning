using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddWalletTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WalletTransactions",
                columns: table => new
                {
                    TransactionId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    RawTransaction = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    BlockHeight = table.Column<long>(type: "bigint", nullable: true),
                    BlockHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: true),
                    Timestamp = table.Column<long>(type: "bigint", nullable: false),
                    OurOutputs = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OurInputs = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletTransactions", x => x.TransactionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransactions_BlockHeight",
                table: "WalletTransactions",
                column: "BlockHeight");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WalletTransactions");
        }
    }
}