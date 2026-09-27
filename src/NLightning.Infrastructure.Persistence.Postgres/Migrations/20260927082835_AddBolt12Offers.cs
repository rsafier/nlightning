using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddBolt12Offers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "bolt12invoice_bytes",
                table: "payments",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "invoice_request_metadata",
                table: "payments",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "offer_bolt12",
                table: "payments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payer_note",
                table: "payments",
                type: "text",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "bolt11",
                table: "invoices",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<byte[]>(
                name: "bolt12invoice_bytes",
                table: "invoices",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "invoice_request_payer_id",
                table: "invoices",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "kind",
                table: "invoices",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<byte[]>(
                name: "offer_id",
                table: "invoices",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payer_note",
                table: "invoices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "quantity",
                table: "invoices",
                type: "numeric(20,0)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "offers",
                columns: table => new
                {
                    offer_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    bolt12 = table.Column<string>(type: "text", nullable: false),
                    offer_bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    amount_msat = table.Column<long>(type: "bigint", nullable: true),
                    currency = table.Column<string>(type: "text", nullable: true),
                    issuer = table.Column<string>(type: "text", nullable: true),
                    quantity_max = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    absolute_expiry = table.Column<long>(type: "bigint", nullable: true),
                    metadata = table.Column<byte[]>(type: "bytea", nullable: false),
                    issuer_kind = table.Column<byte>(type: "smallint", nullable: false),
                    has_paths = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<byte>(type: "smallint", nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                    disabled_at = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_offers", x => x.offer_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_offer_id_status",
                table: "invoices",
                columns: new[] { "offer_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_offers_created_at",
                table: "offers",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_offers_status",
                table: "offers",
                column: "status");

            migrationBuilder.AddForeignKey(
                name: "fk_invoices_offers_offer_id",
                table: "invoices",
                column: "offer_id",
                principalTable: "offers",
                principalColumn: "offer_id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_invoices_offers_offer_id",
                table: "invoices");

            migrationBuilder.DropTable(
                name: "offers");

            migrationBuilder.DropIndex(
                name: "ix_invoices_offer_id_status",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "bolt12invoice_bytes",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "invoice_request_metadata",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "offer_bolt12",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "payer_note",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "bolt12invoice_bytes",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "invoice_request_payer_id",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "offer_id",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "payer_note",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "quantity",
                table: "invoices");

            migrationBuilder.AlterColumn<string>(
                name: "bolt11",
                table: "invoices",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}