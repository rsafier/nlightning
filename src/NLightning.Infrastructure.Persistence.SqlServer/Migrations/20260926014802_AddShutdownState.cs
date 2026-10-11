using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
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
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ClosingTxId",
                table: "Channels",
                type: "varbinary(32)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "LocalShutdownScript",
                table: "Channels",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RemoteShutdownScript",
                table: "Channels",
                type: "varbinary(max)",
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