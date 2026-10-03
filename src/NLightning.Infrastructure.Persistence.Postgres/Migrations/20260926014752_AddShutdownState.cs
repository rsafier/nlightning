using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddShutdownState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "closing_transaction",
                table: "channels",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "closing_tx_id",
                table: "channels",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "local_shutdown_script",
                table: "channels",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "remote_shutdown_script",
                table: "channels",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "closing_transaction",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "closing_tx_id",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "local_shutdown_script",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "remote_shutdown_script",
                table: "channels");
        }
    }
}