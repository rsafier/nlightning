using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
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
                    TransactionId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    RawTransaction = table.Column<byte[]>(type: "BLOB", nullable: false),
                    BlockHeight = table.Column<uint>(type: "INTEGER", nullable: true),
                    BlockHash = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: true),
                    Timestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    OurOutputs = table.Column<string>(type: "TEXT", nullable: false),
                    OurInputs = table.Column<string>(type: "TEXT", nullable: false)
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