using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddLotBuckets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "held_since",
                table: "accounting_lots",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "lender",
                table: "accounting_lots",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "kind",
                table: "accounting_lot_reliefs",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "held_since",
                table: "accounting_lots");

            migrationBuilder.DropColumn(
                name: "lender",
                table: "accounting_lots");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "accounting_lot_reliefs");
        }
    }
}