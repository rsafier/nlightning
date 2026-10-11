using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedDatabaseHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AccountingEvents_LedgerSeq",
                table: "AccountingEvents");

            migrationBuilder.CreateTable(
                name: "ChannelKeyIndexHighWater",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    HighWater = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelKeyIndexHighWater", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MacaroonRootKeys",
                columns: table => new
                {
                    RootKeyId = table.Column<long>(type: "INTEGER", nullable: false),
                    Nonce = table.Column<byte[]>(type: "BLOB", maxLength: 24, nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "BLOB", maxLength: 48, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MacaroonRootKeys", x => x.RootKeyId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEvents_LedgerSeq",
                table: "AccountingEvents",
                column: "LedgerSeq",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelKeyIndexHighWater");

            migrationBuilder.DropTable(
                name: "MacaroonRootKeys");

            migrationBuilder.DropIndex(
                name: "IX_AccountingEvents_LedgerSeq",
                table: "AccountingEvents");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEvents_LedgerSeq",
                table: "AccountingEvents",
                column: "LedgerSeq");
        }
    }
}