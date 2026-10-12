using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoicesPaymentsAndCircuits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "origin_incoming_channel_id",
                table: "htlcs",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "origin_incoming_htlc_id",
                table: "htlcs",
                type: "numeric(20,0)",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "origin_kind",
                table: "htlcs",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "origin_payment_hash",
                table: "htlcs",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "max_dust_htlc_exposure_msat",
                table: "channels",
                type: "numeric(20,0)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "forward_circuits",
                columns: table => new
                {
                    incoming_channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    incoming_htlc_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    incoming_amount_msat = table.Column<long>(type: "bigint", nullable: false),
                    incoming_cltv_expiry = table.Column<long>(type: "bigint", nullable: false),
                    payment_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    incoming_shared_secret = table.Column<byte[]>(type: "bytea", nullable: false),
                    outgoing_short_channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    outgoing_amount_msat = table.Column<long>(type: "bigint", nullable: false),
                    outgoing_cltv_expiry = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<byte>(type: "smallint", nullable: false),
                    outgoing_channel_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    outgoing_htlc_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    resolved_at = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_forward_circuits", x => new { x.incoming_channel_id, x.incoming_htlc_id });
                });

            migrationBuilder.CreateTable(
                name: "invoices",
                columns: table => new
                {
                    payment_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    preimage = table.Column<byte[]>(type: "bytea", nullable: false),
                    payment_secret = table.Column<byte[]>(type: "bytea", nullable: false),
                    amount_msat = table.Column<long>(type: "bigint", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    bolt11 = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                    expiry_seconds = table.Column<long>(type: "bigint", nullable: false),
                    min_final_cltv_expiry = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<byte>(type: "smallint", nullable: false),
                    amount_received_msat = table.Column<long>(type: "bigint", nullable: true),
                    settled_at = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoices", x => x.payment_hash);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    payment_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    bolt11 = table.Column<string>(type: "text", nullable: true),
                    payee_node_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    amount_msat = table.Column<long>(type: "bigint", nullable: false),
                    fee_msat = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<byte>(type: "smallint", nullable: false),
                    outgoing_channel_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    outgoing_htlc_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    preimage = table.Column<byte[]>(type: "bytea", nullable: true),
                    failure_code = table.Column<int>(type: "integer", nullable: true),
                    failure_source_index = table.Column<int>(type: "integer", nullable: true),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    completed_at = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.payment_hash);
                });

            migrationBuilder.CreateTable(
                name: "payment_hops",
                columns: table => new
                {
                    payment_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    hop_index = table.Column<byte>(type: "smallint", nullable: false),
                    node_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    short_channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    amount_msat = table.Column<long>(type: "bigint", nullable: false),
                    cltv_expiry = table.Column<long>(type: "bigint", nullable: false),
                    shared_secret = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_hops", x => new { x.payment_hash, x.hop_index });
                    table.ForeignKey(
                        name: "fk_payment_hops_payments_payment_hash",
                        column: x => x.payment_hash,
                        principalTable: "payments",
                        principalColumn: "payment_hash",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_htlcs_origin_incoming_channel_id_origin_incoming_htlc_id",
                table: "htlcs",
                columns: new[] { "origin_incoming_channel_id", "origin_incoming_htlc_id" });

            migrationBuilder.CreateIndex(
                name: "ix_htlcs_origin_payment_hash",
                table: "htlcs",
                column: "origin_payment_hash");

            migrationBuilder.CreateIndex(
                name: "ix_forward_circuits_outgoing_channel_id_outgoing_htlc_id",
                table: "forward_circuits",
                columns: new[] { "outgoing_channel_id", "outgoing_htlc_id" });

            migrationBuilder.CreateIndex(
                name: "ix_forward_circuits_status",
                table: "forward_circuits",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_created_at",
                table: "invoices",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_payments_created_at",
                table: "payments",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_payments_status",
                table: "payments",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "forward_circuits");

            migrationBuilder.DropTable(
                name: "invoices");

            migrationBuilder.DropTable(
                name: "payment_hops");

            migrationBuilder.DropTable(
                name: "payments");

            migrationBuilder.DropIndex(
                name: "ix_htlcs_origin_incoming_channel_id_origin_incoming_htlc_id",
                table: "htlcs");

            migrationBuilder.DropIndex(
                name: "ix_htlcs_origin_payment_hash",
                table: "htlcs");

            migrationBuilder.DropColumn(
                name: "origin_incoming_channel_id",
                table: "htlcs");

            migrationBuilder.DropColumn(
                name: "origin_incoming_htlc_id",
                table: "htlcs");

            migrationBuilder.DropColumn(
                name: "origin_kind",
                table: "htlcs");

            migrationBuilder.DropColumn(
                name: "origin_payment_hash",
                table: "htlcs");

            migrationBuilder.DropColumn(
                name: "max_dust_htlc_exposure_msat",
                table: "channels");
        }
    }
}