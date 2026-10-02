using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddMutualCloseTerms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "CloseProtocol",
                table: "Channels",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LocalIsCloser",
                table: "Channels",
                type: "bit",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CloseProtocol",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "LocalIsCloser",
                table: "Channels");
        }
    }
}