using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddForwardCircuitFailureReasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "failure_code",
                table: "forward_circuits",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "failure_source",
                table: "forward_circuits",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "failure_code",
                table: "forward_circuits");

            migrationBuilder.DropColumn(
                name: "failure_source",
                table: "forward_circuits");
        }
    }
}