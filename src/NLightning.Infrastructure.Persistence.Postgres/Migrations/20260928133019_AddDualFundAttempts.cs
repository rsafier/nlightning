using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddDualFundAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "local_funding_satoshis",
                table: "interactive_tx_sessions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "their_commitment_signature",
                table: "interactive_tx_sessions",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "local_funding_satoshis",
                table: "interactive_tx_sessions");

            migrationBuilder.DropColumn(
                name: "their_commitment_signature",
                table: "interactive_tx_sessions");
        }
    }
}