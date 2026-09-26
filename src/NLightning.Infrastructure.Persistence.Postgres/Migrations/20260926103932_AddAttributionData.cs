using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddAttributionData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "hold_time_ms",
                table: "payment_hops",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "added_at",
                table: "htlcs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "attribution_data",
                table: "htlcs",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "fulfillment_payload",
                table: "htlcs",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "hold_time_ms",
                table: "payment_hops");

            migrationBuilder.DropColumn(
                name: "added_at",
                table: "htlcs");

            migrationBuilder.DropColumn(
                name: "attribution_data",
                table: "htlcs");

            migrationBuilder.DropColumn(
                name: "fulfillment_payload",
                table: "htlcs");
        }
    }
}