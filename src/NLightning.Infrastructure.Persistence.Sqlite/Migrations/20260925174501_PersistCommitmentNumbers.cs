using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class PersistCommitmentNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<ulong>(
                name: "LocalCommitmentNumber",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0ul);

            migrationBuilder.AddColumn<ulong>(
                name: "RemoteCommitmentNumber",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0ul);

            // Existing rows are at rest (no commitment dance in flight), where the current commitment number of a
            // side equals the number of its revoked commitments (NL-188).
            migrationBuilder.Sql(
                "UPDATE \"Channels\" SET \"LocalCommitmentNumber\" = \"LocalRevocationNumber\", \"RemoteCommitmentNumber\" = \"RemoteRevocationNumber\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LocalCommitmentNumber",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "RemoteCommitmentNumber",
                table: "Channels");
        }
    }
}