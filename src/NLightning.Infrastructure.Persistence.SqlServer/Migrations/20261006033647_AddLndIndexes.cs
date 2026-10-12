using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
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
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AddIndex",
                table: "Invoices",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "Htlcs",
                table: "Invoices",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SettleIndex",
                table: "Invoices",
                type: "bigint",
                nullable: true);

            // ---- Hand-written data step (NL-1165): the rows that exist get LND's dense indexes in creation order (settle
            // order for settle_index; trampoline relay legs get none, as the allocator gives them none) ----
            migrationBuilder.Sql("WITH r AS (SELECT [AddIndex], ROW_NUMBER() OVER (ORDER BY [CreatedAt], [PaymentHash]) AS n FROM [Invoices]) UPDATE r SET [AddIndex] = n;");
            migrationBuilder.Sql("WITH r AS (SELECT [SettleIndex], ROW_NUMBER() OVER (ORDER BY [SettledAt], [PaymentHash]) AS n FROM [Invoices] WHERE [Status] = 2) UPDATE r SET [SettleIndex] = n;");
            migrationBuilder.Sql("WITH r AS (SELECT [PaymentIndex], ROW_NUMBER() OVER (ORDER BY [CreatedAt], [PaymentHash]) AS n FROM [Payments] WHERE [IsTrampolineRelay] = 0) UPDATE r SET [PaymentIndex] = n;");
            // ---- End of the hand-written data step ----

            migrationBuilder.CreateIndex(
                name: "IX_Payments_PaymentIndex",
                table: "Payments",
                column: "PaymentIndex",
                unique: true,
                filter: "[PaymentIndex] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_AddIndex",
                table: "Invoices",
                column: "AddIndex",
                unique: true,
                filter: "[AddIndex] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_SettleIndex",
                table: "Invoices",
                column: "SettleIndex",
                unique: true,
                filter: "[SettleIndex] IS NOT NULL");
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