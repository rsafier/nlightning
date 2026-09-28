using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddDualFundAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LocalFundingSatoshis",
                table: "InteractiveTxSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "TheirCommitmentSignature",
                table: "InteractiveTxSessions",
                type: "BLOB",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LocalFundingSatoshis",
                table: "InteractiveTxSessions");

            migrationBuilder.DropColumn(
                name: "TheirCommitmentSignature",
                table: "InteractiveTxSessions");
        }
    }
}