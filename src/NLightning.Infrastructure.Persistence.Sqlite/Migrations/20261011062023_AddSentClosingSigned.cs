using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddSentClosingSigned : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastSentClosingFeeSatoshis",
                table: "Channels",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "LastSentClosingSignature",
                table: "Channels",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "LastSentClosingTxId",
                table: "Channels",
                type: "BLOB",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSentClosingFeeSatoshis",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "LastSentClosingSignature",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "LastSentClosingTxId",
                table: "Channels");
        }
    }
}