using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddCashuProcessorQuotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cashu_deposits",
                columns: table => new
                {
                    tx_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    output_index = table.Column<long>(type: "bigint", nullable: false),
                    quote_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    amount_sat = table.Column<long>(type: "bigint", nullable: false),
                    block_height = table.Column<long>(type: "bigint", nullable: false),
                    reported_at = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cashu_deposits", x => new { x.tx_id, x.output_index });
                });

            migrationBuilder.CreateTable(
                name: "cashu_quotes",
                columns: table => new
                {
                    quote_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    method = table.Column<byte>(type: "smallint", nullable: false),
                    direction = table.Column<byte>(type: "smallint", nullable: false),
                    amount_msat = table.Column<long>(type: "bigint", nullable: false),
                    max_fee_msat = table.Column<long>(type: "bigint", nullable: true),
                    fee_msat = table.Column<long>(type: "bigint", nullable: true),
                    payment_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    address = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    request = table.Column<string>(type: "text", nullable: true),
                    fee_index = table.Column<long>(type: "bigint", nullable: true),
                    tx_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    output_index = table.Column<long>(type: "bigint", nullable: true),
                    state = table.Column<byte>(type: "smallint", nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cashu_quotes", x => x.quote_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cashu_deposits_quote_id",
                table: "cashu_deposits",
                column: "quote_id");

            migrationBuilder.CreateIndex(
                name: "ix_cashu_deposits_reported_at",
                table: "cashu_deposits",
                column: "reported_at");

            migrationBuilder.CreateIndex(
                name: "ix_cashu_quotes_address",
                table: "cashu_quotes",
                column: "address");

            migrationBuilder.CreateIndex(
                name: "ix_cashu_quotes_direction_method_state",
                table: "cashu_quotes",
                columns: new[] { "direction", "method", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_cashu_quotes_payment_hash",
                table: "cashu_quotes",
                column: "payment_hash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cashu_deposits");

            migrationBuilder.DropTable(
                name: "cashu_quotes");
        }
    }
}