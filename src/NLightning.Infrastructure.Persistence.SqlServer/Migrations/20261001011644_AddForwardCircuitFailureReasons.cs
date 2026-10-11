using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddForwardCircuitFailureReasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FailureCode",
                table: "ForwardCircuits",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "FailureSource",
                table: "ForwardCircuits",
                type: "varbinary(32)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailureCode",
                table: "ForwardCircuits");

            migrationBuilder.DropColumn(
                name: "FailureSource",
                table: "ForwardCircuits");
        }
    }
}