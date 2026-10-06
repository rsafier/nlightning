using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddLndIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PaymentIndex",
                table: "Payments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AddIndex",
                table: "Invoices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "Htlcs",
                table: "Invoices",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SettleIndex",
                table: "Invoices",
                type: "INTEGER",
                nullable: true);

            // ---- Hand-written data step (NL-1165): the rows that exist get LND's dense indexes in creation order (settle
            // order for settle_index; trampoline relay legs get none, as the allocator gives them none) ----
            migrationBuilder.Sql("UPDATE \"Invoices\" SET \"AddIndex\" = r.n FROM (SELECT \"PaymentHash\" AS h, ROW_NUMBER() OVER (ORDER BY \"CreatedAt\", \"PaymentHash\") AS n FROM \"Invoices\") AS r WHERE \"Invoices\".\"PaymentHash\" = r.h;");
            migrationBuilder.Sql("UPDATE \"Invoices\" SET \"SettleIndex\" = r.n FROM (SELECT \"PaymentHash\" AS h, ROW_NUMBER() OVER (ORDER BY \"SettledAt\", \"PaymentHash\") AS n FROM \"Invoices\" WHERE \"Status\" = 2) AS r WHERE \"Invoices\".\"PaymentHash\" = r.h;");
            migrationBuilder.Sql("UPDATE \"Payments\" SET \"PaymentIndex\" = r.n FROM (SELECT \"PaymentHash\" AS h, ROW_NUMBER() OVER (ORDER BY \"CreatedAt\", \"PaymentHash\") AS n FROM \"Payments\" WHERE NOT \"IsTrampolineRelay\") AS r WHERE \"Payments\".\"PaymentHash\" = r.h;");
            // ---- End of the hand-written data step ----

            migrationBuilder.CreateIndex(
                name: "IX_Payments_PaymentIndex",
                table: "Payments",
                column: "PaymentIndex",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_AddIndex",
                table: "Invoices",
                column: "AddIndex",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_SettleIndex",
                table: "Invoices",
                column: "SettleIndex",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_PaymentIndex",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_AddIndex",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_SettleIndex",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "PaymentIndex",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "AddIndex",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Htlcs",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "SettleIndex",
                table: "Invoices");
        }
    }
}