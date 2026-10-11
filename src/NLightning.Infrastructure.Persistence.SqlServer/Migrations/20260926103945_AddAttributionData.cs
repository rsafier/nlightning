using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddAttributionData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "HoldTimeMs",
                table: "PaymentHops",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AddedAt",
                table: "Htlcs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "AttributionData",
                table: "Htlcs",
                type: "varbinary(920)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "FulfillmentPayload",
                table: "Htlcs",
                type: "varbinary(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HoldTimeMs",
                table: "PaymentHops");

            migrationBuilder.DropColumn(
                name: "AddedAt",
                table: "Htlcs");

            migrationBuilder.DropColumn(
                name: "AttributionData",
                table: "Htlcs");

            migrationBuilder.DropColumn(
                name: "FulfillmentPayload",
                table: "Htlcs");
        }
    }
}