using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class PersistCommitmentNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "local_commitment_number",
                table: "channels",
                type: "numeric(20,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "remote_commitment_number",
                table: "channels",
                type: "numeric(20,0)",
                nullable: false,
                defaultValue: 0m);

            // Existing rows are at rest (no commitment dance in flight), where the current commitment number of a
            // side equals the number of its revoked commitments (NL-188).
            migrationBuilder.Sql(
                "UPDATE channels SET local_commitment_number = local_revocation_number, remote_commitment_number = remote_revocation_number;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "local_commitment_number",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "remote_commitment_number",
                table: "channels");
        }
    }
}