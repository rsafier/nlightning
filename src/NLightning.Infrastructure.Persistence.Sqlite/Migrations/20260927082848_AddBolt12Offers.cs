using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddBolt12Offers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "Bolt12InvoiceBytes",
                table: "Payments",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "InvoiceRequestMetadata",
                table: "Payments",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OfferBolt12",
                table: "Payments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayerNote",
                table: "Payments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Bolt11",
                table: "Invoices",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<byte[]>(
                name: "Bolt12InvoiceBytes",
                table: "Invoices",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "InvoiceRequestPayerId",
                table: "Invoices",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "Kind",
                table: "Invoices",
                type: "INTEGER",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<byte[]>(
                name: "OfferId",
                table: "Invoices",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayerNote",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<ulong>(
                name: "Quantity",
                table: "Invoices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Offers",
                columns: table => new
                {
                    OfferId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Bolt12 = table.Column<string>(type: "TEXT", nullable: false),
                    OfferBytes = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    AmountMsat = table.Column<long>(type: "INTEGER", nullable: true),
                    Currency = table.Column<string>(type: "TEXT", nullable: true),
                    Issuer = table.Column<string>(type: "TEXT", nullable: true),
                    QuantityMax = table.Column<ulong>(type: "INTEGER", nullable: true),
                    AbsoluteExpiry = table.Column<long>(type: "INTEGER", nullable: true),
                    Metadata = table.Column<byte[]>(type: "BLOB", nullable: false),
                    IssuerKind = table.Column<byte>(type: "INTEGER", nullable: false),
                    HasPaths = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<byte>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DisabledAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Offers", x => x.OfferId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_OfferId_Status",
                table: "Invoices",
                columns: new[] { "OfferId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Offers_CreatedAt",
                table: "Offers",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Offers_Status",
                table: "Offers",
                column: "Status");

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_Offers_OfferId",
                table: "Invoices",
                column: "OfferId",
                principalTable: "Offers",
                principalColumn: "OfferId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_Offers_OfferId",
                table: "Invoices");

            migrationBuilder.DropTable(
                name: "Offers");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_OfferId_Status",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Bolt12InvoiceBytes",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "InvoiceRequestMetadata",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "OfferBolt12",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PayerNote",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "Bolt12InvoiceBytes",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "InvoiceRequestPayerId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "OfferId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "PayerNote",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Quantity",
                table: "Invoices");

            migrationBuilder.AlterColumn<string>(
                name: "Bolt11",
                table: "Invoices",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}