using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class IndexImportedHistoryAndRecoveryKeyState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FundingKeysUnknown",
                table: "ChannelFundings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Older recovery rows may contain copied keys whose aggregate never matched the funding output.
            // Conservatively mark their hints unknown; payment-basepoint recovery remains available.
            migrationBuilder.Sql("UPDATE ChannelFundings SET FundingKeysUnknown = 1 WHERE ChannelId IN (SELECT c.ChannelId FROM Channels c JOIN ChannelConfigs cc ON cc.ChannelId = c.ChannelId WHERE c.DataLossDetected = 1 AND cc.OptionSimpleTaproot = 1 AND c.LastReceivedSignature IS NULL AND c.LastSentSignature IS NULL)");

            migrationBuilder.CreateTable(
                name: "ImportedWatchIndexes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Height = table.Column<long>(type: "bigint", nullable: false),
                    BlockHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    ScriptSet = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    History = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedWatchIndexes", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportedWatchIndexes");

            migrationBuilder.DropColumn(
                name: "FundingKeysUnknown",
                table: "ChannelFundings");
        }
    }
}