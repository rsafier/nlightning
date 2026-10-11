using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddOnionReplaySet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OnionReplayEntries",
                columns: table => new
                {
                    Hmac = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    HtlcId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    ExpiryHeight = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnionReplayEntries", x => x.Hmac);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OnionReplayEntries_ExpiryHeight",
                table: "OnionReplayEntries",
                column: "ExpiryHeight");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OnionReplayEntries");
        }
    }
}