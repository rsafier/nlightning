using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentCustomRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "CustomRecords",
                table: "Payments",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "CustomRecords",
                table: "Invoices",
                type: "varbinary(max)",
                nullable: true);

            // NL-460: the keysend rows' custom records move out of the BOLT 12 invoice bytes they were stored in.
            // Invoices: Kind 2 is a keysend record. Payments: a keysend payment has no invoice string and no offer,
            // so its records are the bytes (two statements, so no column reads the other's new value)
            migrationBuilder.Sql(
                "UPDATE Invoices SET CustomRecords = Bolt12InvoiceBytes WHERE Kind = 2;");
            migrationBuilder.Sql(
                "UPDATE Invoices SET Bolt12InvoiceBytes = NULL WHERE Kind = 2;");
            migrationBuilder.Sql(
                "UPDATE Payments SET CustomRecords = Bolt12InvoiceBytes " +
                "WHERE Bolt11 IS NULL AND OfferBolt12 IS NULL AND Bolt12InvoiceBytes IS NOT NULL;");
            migrationBuilder.Sql(
                "UPDATE Payments SET Bolt12InvoiceBytes = NULL " +
                "WHERE Bolt11 IS NULL AND OfferBolt12 IS NULL AND CustomRecords IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE Invoices SET Bolt12InvoiceBytes = CustomRecords WHERE Kind = 2;");
            migrationBuilder.Sql(
                "UPDATE Invoices SET CustomRecords = NULL WHERE Kind = 2;");
            migrationBuilder.Sql(
                "UPDATE Payments SET Bolt12InvoiceBytes = CustomRecords " +
                "WHERE Bolt11 IS NULL AND OfferBolt12 IS NULL AND Bolt12InvoiceBytes IS NULL " +
                "AND CustomRecords IS NOT NULL;");
            migrationBuilder.Sql(
                "UPDATE Payments SET CustomRecords = NULL WHERE Bolt11 IS NULL AND OfferBolt12 IS NULL;");

            migrationBuilder.DropColumn(
                name: "CustomRecords",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "CustomRecords",
                table: "Invoices");
        }
    }
}