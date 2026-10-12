using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddLiquidityPurchases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "liquidity_purchases",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    funding_tx_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    role = table.Column<byte>(type: "smallint", nullable: false),
                    kind = table.Column<byte>(type: "smallint", nullable: false),
                    requested_sat = table.Column<long>(type: "bigint", nullable: false),
                    contributed_sat = table.Column<long>(type: "bigint", nullable: false),
                    rate_min_amount_sat = table.Column<long>(type: "bigint", nullable: false),
                    rate_max_amount_sat = table.Column<long>(type: "bigint", nullable: false),
                    rate_funding_weight = table.Column<int>(type: "integer", nullable: false),
                    rate_fee_basis = table.Column<int>(type: "integer", nullable: false),
                    rate_fee_base_sat = table.Column<long>(type: "bigint", nullable: false),
                    rate_channel_creation_fee_sat = table.Column<long>(type: "bigint", nullable: false),
                    payment_type = table.Column<byte>(type: "smallint", nullable: false),
                    mining_fee_sat = table.Column<long>(type: "bigint", nullable: false),
                    service_fee_sat = table.Column<long>(type: "bigint", nullable: false),
                    signature = table.Column<byte[]>(type: "bytea", nullable: false),
                    funding_script = table.Column<byte[]>(type: "bytea", nullable: false),
                    peer_node_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    lease_blocks = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<byte>(type: "smallint", nullable: false),
                    lease_start_height = table.Column<long>(type: "bigint", nullable: true),
                    closed_at_height = table.Column<long>(type: "bigint", nullable: true),
                    closed_early = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_liquidity_purchases", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_liquidity_purchases_channel_id_funding_tx_id",
                table: "liquidity_purchases",
                columns: new[] { "channel_id", "funding_tx_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_liquidity_purchases_created_at",
                table: "liquidity_purchases",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_liquidity_purchases_status",
                table: "liquidity_purchases",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "liquidity_purchases");
        }
    }
}