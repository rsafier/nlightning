using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
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
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "TheirCommitmentSignature",
                table: "InteractiveTxSessions",
                type: "varbinary(64)",
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