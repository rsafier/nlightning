using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddWalletTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wallet_transactions",
                columns: table => new
                {
                    transaction_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    raw_transaction = table.Column<byte[]>(type: "bytea", nullable: false),
                    block_height = table.Column<long>(type: "bigint", nullable: true),
                    block_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: true),
                    timestamp = table.Column<long>(type: "bigint", nullable: false),
                    our_outputs = table.Column<string>(type: "text", nullable: false),
                    our_inputs = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallet_transactions", x => x.transaction_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_wallet_transactions_block_height",
                table: "wallet_transactions",
                column: "block_height");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wallet_transactions");
        }
    }
}