using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddLndIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "payment_index",
                table: "payments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "add_index",
                table: "invoices",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "htlcs",
                table: "invoices",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "settle_index",
                table: "invoices",
                type: "bigint",
                nullable: true);

            // ---- Hand-written data step (NL-1165): the rows that exist get LND's dense indexes in creation order (settle
            // order for settle_index; trampoline relay legs get none, as the allocator gives them none) ----
            migrationBuilder.Sql("UPDATE invoices SET add_index = r.n FROM (SELECT payment_hash AS h, ROW_NUMBER() OVER (ORDER BY created_at, payment_hash) AS n FROM invoices) AS r WHERE invoices.payment_hash = r.h;");
            migrationBuilder.Sql("UPDATE invoices SET settle_index = r.n FROM (SELECT payment_hash AS h, ROW_NUMBER() OVER (ORDER BY settled_at, payment_hash) AS n FROM invoices WHERE status = 2) AS r WHERE invoices.payment_hash = r.h;");
            migrationBuilder.Sql("UPDATE payments SET payment_index = r.n FROM (SELECT payment_hash AS h, ROW_NUMBER() OVER (ORDER BY created_at, payment_hash) AS n FROM payments WHERE NOT is_trampoline_relay) AS r WHERE payments.payment_hash = r.h;");
            // ---- End of the hand-written data step ----

            migrationBuilder.CreateIndex(
                name: "ix_payments_payment_index",
                table: "payments",
                column: "payment_index",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_add_index",
                table: "invoices",
                column: "add_index",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_settle_index",
                table: "invoices",
                column: "settle_index",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_payments_payment_index",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "ix_invoices_add_index",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "ix_invoices_settle_index",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "payment_index",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "add_index",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "htlcs",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "settle_index",
                table: "invoices");
        }
    }
}