using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSentClosingSigned : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "last_sent_closing_fee_satoshis",
                table: "channels",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "last_sent_closing_signature",
                table: "channels",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "last_sent_closing_tx_id",
                table: "channels",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_sent_closing_fee_satoshis",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "last_sent_closing_signature",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "last_sent_closing_tx_id",
                table: "channels");
        }
    }
}