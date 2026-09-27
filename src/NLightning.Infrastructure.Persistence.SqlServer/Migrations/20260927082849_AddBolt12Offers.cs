using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
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
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "InvoiceRequestMetadata",
                table: "Payments",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OfferBolt12",
                table: "Payments",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayerNote",
                table: "Payments",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Bolt11",
                table: "Invoices",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AddColumn<byte[]>(
                name: "Bolt12InvoiceBytes",
                table: "Invoices",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "InvoiceRequestPayerId",
                table: "Invoices",
                type: "varbinary(33)",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "Kind",
                table: "Invoices",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<byte[]>(
                name: "OfferId",
                table: "Invoices",
                type: "varbinary(32)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayerNote",
                table: "Invoices",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Quantity",
                table: "Invoices",
                type: "decimal(20,0)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Offers",
                columns: table => new
                {
                    OfferId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    Bolt12 = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OfferBytes = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AmountMsat = table.Column<long>(type: "bigint", nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Issuer = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    QuantityMax = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    AbsoluteExpiry = table.Column<long>(type: "bigint", nullable: true),
                    Metadata = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    IssuerKind = table.Column<byte>(type: "tinyint", nullable: false),
                    HasPaths = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    DisabledAt = table.Column<long>(type: "bigint", nullable: true)
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
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);
        }
    }
}