using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddLotBuckets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "HeldSince",
                table: "AccountingLots",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Lender",
                table: "AccountingLots",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "Kind",
                table: "AccountingLotReliefs",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HeldSince",
                table: "AccountingLots");

            migrationBuilder.DropColumn(
                name: "Lender",
                table: "AccountingLots");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "AccountingLotReliefs");
        }
    }
}