using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
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
                    Hmac = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    ChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    HtlcId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    ExpiryHeight = table.Column<long>(type: "bigint", nullable: false)
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