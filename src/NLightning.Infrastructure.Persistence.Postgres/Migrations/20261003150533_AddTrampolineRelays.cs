using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddTrampolineRelays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_trampoline_relay",
                table: "payments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "payment_trampoline_hops",
                columns: table => new
                {
                    payment_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    hop_index = table.Column<int>(type: "integer", nullable: false),
                    node_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    shared_secret = table.Column<byte[]>(type: "bytea", nullable: false),
                    amount_msat = table.Column<long>(type: "bigint", nullable: false),
                    cltv_expiry = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_trampoline_hops", x => new { x.payment_hash, x.attempt, x.hop_index });
                });

            migrationBuilder.CreateTable(
                name: "trampoline_relays",
                columns: table => new
                {
                    payment_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<byte>(type: "smallint", nullable: false),
                    next_node_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    next_encrypted_recipient_data = table.Column<byte[]>(type: "bytea", nullable: true),
                    next_path_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    recipient_features = table.Column<byte[]>(type: "bytea", nullable: true),
                    recipient_blinded_paths = table.Column<byte[]>(type: "bytea", nullable: true),
                    next_trampoline_packet = table.Column<byte[]>(type: "bytea", nullable: true),
                    amount_out_msat = table.Column<long>(type: "bigint", nullable: false),
                    cltv_expiry_out = table.Column<long>(type: "bigint", nullable: false),
                    incoming_total_msat = table.Column<long>(type: "bigint", nullable: false),
                    fee_earned_msat = table.Column<long>(type: "bigint", nullable: true),
                    outgoing_payment_secret = table.Column<byte[]>(type: "bytea", nullable: true),
                    preimage = table.Column<byte[]>(type: "bytea", nullable: true),
                    failure_code = table.Column<int>(type: "integer", nullable: true),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                    completed_at = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trampoline_relays", x => x.payment_hash);
                });

            migrationBuilder.CreateTable(
                name: "trampoline_relay_parts",
                columns: table => new
                {
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    htlc_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    payment_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    amount_msat = table.Column<long>(type: "bigint", nullable: false),
                    cltv_expiry = table.Column<long>(type: "bigint", nullable: false),
                    outer_shared_secret = table.Column<byte[]>(type: "bytea", nullable: false),
                    trampoline_shared_secret = table.Column<byte[]>(type: "bytea", nullable: false),
                    outer_payment_secret = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trampoline_relay_parts", x => new { x.channel_id, x.htlc_id });
                    table.ForeignKey(
                        name: "fk_trampoline_relay_parts_trampoline_relays_payment_hash",
                        column: x => x.payment_hash,
                        principalTable: "trampoline_relays",
                        principalColumn: "payment_hash",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_trampoline_relay_parts_payment_hash",
                table: "trampoline_relay_parts",
                column: "payment_hash");

            migrationBuilder.CreateIndex(
                name: "ix_trampoline_relays_created_at",
                table: "trampoline_relays",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_trampoline_relays_status",
                table: "trampoline_relays",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_trampoline_hops");

            migrationBuilder.DropTable(
                name: "trampoline_relay_parts");

            migrationBuilder.DropTable(
                name: "trampoline_relays");

            migrationBuilder.DropColumn(
                name: "is_trampoline_relay",
                table: "payments");
        }
    }
}