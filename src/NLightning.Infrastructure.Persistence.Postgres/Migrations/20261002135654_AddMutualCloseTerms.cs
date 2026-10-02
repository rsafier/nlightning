using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddMutualCloseTerms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "close_protocol",
                table: "channels",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "local_is_closer",
                table: "channels",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "close_protocol",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "local_is_closer",
                table: "channels");
        }
    }
}