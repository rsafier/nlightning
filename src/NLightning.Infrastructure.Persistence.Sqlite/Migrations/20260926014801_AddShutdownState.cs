using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddShutdownState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "ClosingTransaction",
                table: "Channels",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ClosingTxId",
                table: "Channels",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "LocalShutdownScript",
                table: "Channels",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RemoteShutdownScript",
                table: "Channels",
                type: "BLOB",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClosingTransaction",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "ClosingTxId",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "LocalShutdownScript",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "RemoteShutdownScript",
                table: "Channels");
        }
    }
}