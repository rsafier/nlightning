using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentCustomRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "custom_records",
                table: "payments",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "custom_records",
                table: "invoices",
                type: "bytea",
                nullable: true);

            // NL-460: the keysend rows' custom records move out of the BOLT 12 invoice bytes they were stored in.
            // Invoices: Kind 2 is a keysend record. Payments: a keysend payment has no invoice string and no offer,
            // so its records are the bytes (two statements, so no column reads the other's new value)
            migrationBuilder.Sql(
                "UPDATE invoices SET custom_records = bolt12invoice_bytes WHERE kind = 2;");
            migrationBuilder.Sql(
                "UPDATE invoices SET bolt12invoice_bytes = NULL WHERE kind = 2;");
            migrationBuilder.Sql(
                "UPDATE payments SET custom_records = bolt12invoice_bytes " +
                "WHERE bolt11 IS NULL AND offer_bolt12 IS NULL AND bolt12invoice_bytes IS NOT NULL;");
            migrationBuilder.Sql(
                "UPDATE payments SET bolt12invoice_bytes = NULL " +
                "WHERE bolt11 IS NULL AND offer_bolt12 IS NULL AND custom_records IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE invoices SET bolt12invoice_bytes = custom_records WHERE kind = 2;");
            migrationBuilder.Sql(
                "UPDATE invoices SET custom_records = NULL WHERE kind = 2;");
            migrationBuilder.Sql(
                "UPDATE payments SET bolt12invoice_bytes = custom_records " +
                "WHERE bolt11 IS NULL AND offer_bolt12 IS NULL AND bolt12invoice_bytes IS NULL " +
                "AND custom_records IS NOT NULL;");
            migrationBuilder.Sql(
                "UPDATE payments SET custom_records = NULL WHERE bolt11 IS NULL AND offer_bolt12 IS NULL;");

            migrationBuilder.DropColumn(
                name: "custom_records",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "custom_records",
                table: "invoices");
        }
    }
}