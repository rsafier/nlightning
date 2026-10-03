using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSimpleTaprootChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "partial_signature",
                table: "commitments",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "last_received_partial_signature",
                table: "channels",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "remote_next_nonces",
                table: "channels",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "option_simple_taproot",
                table: "channel_configs",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "partial_signature",
                table: "commitments");

            migrationBuilder.DropColumn(
                name: "last_received_partial_signature",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "remote_next_nonces",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "option_simple_taproot",
                table: "channel_configs");
        }
    }
}
