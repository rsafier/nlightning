using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_parts",
                columns: table => new
                {
                    payment_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    part_index = table.Column<byte>(type: "smallint", nullable: false),
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    htlc_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    state = table.Column<byte>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_parts", x => new { x.payment_hash, x.part_index });
                    table.ForeignKey(
                        name: "fk_payment_parts_payments_payment_hash",
                        column: x => x.payment_hash,
                        principalTable: "payments",
                        principalColumn: "payment_hash",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payment_part_hops",
                columns: table => new
                {
                    payment_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    part_index = table.Column<byte>(type: "smallint", nullable: false),
                    hop_index = table.Column<byte>(type: "smallint", nullable: false),
                    node_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    short_channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    amount_msat = table.Column<long>(type: "bigint", nullable: false),
                    cltv_expiry = table.Column<long>(type: "bigint", nullable: false),
                    shared_secret = table.Column<byte[]>(type: "bytea", nullable: false),
                    hold_time_ms = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_part_hops", x => new { x.payment_hash, x.part_index, x.hop_index });
                    table.ForeignKey(
                        name: "fk_payment_part_hops_payment_parts_payment_hash_part_index",
                        columns: x => new { x.payment_hash, x.part_index },
                        principalTable: "payment_parts",
                        principalColumns: new[] { "payment_hash", "part_index" },
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_part_hops");

            migrationBuilder.DropTable(
                name: "payment_parts");
        }
    }
}