using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddChainWatchAndBroadcasts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "block_headers",
                columns: table => new
                {
                    height = table.Column<long>(type: "bigint", nullable: false),
                    block_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    previous_block_hash = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_block_headers", x => x.height);
                });

            migrationBuilder.CreateTable(
                name: "broadcast_transactions",
                columns: table => new
                {
                    transaction_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    raw_transaction = table.Column<byte[]>(type: "bytea", nullable: false),
                    purpose = table.Column<byte>(type: "smallint", nullable: false),
                    feerate_per_kw = table.Column<long>(type: "bigint", nullable: false),
                    replaces_transaction_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    first_broadcast_height = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<byte>(type: "smallint", nullable: false),
                    confirmed_height = table.Column<long>(type: "bigint", nullable: true),
                    confirmed_block_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_broadcast_transactions", x => x.transaction_id);
                });

            migrationBuilder.CreateTable(
                name: "watched_outpoints",
                columns: table => new
                {
                    transaction_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    output_index = table.Column<long>(type: "bigint", nullable: false),
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    purpose = table.Column<byte>(type: "smallint", nullable: false),
                    spent_by_transaction_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    spent_at_height = table.Column<long>(type: "bigint", nullable: true),
                    spent_block_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_watched_outpoints", x => new { x.transaction_id, x.output_index });
                });

            migrationBuilder.CreateIndex(
                name: "ix_broadcast_transactions_channel_id",
                table: "broadcast_transactions",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_broadcast_transactions_confirmed_height",
                table: "broadcast_transactions",
                column: "confirmed_height");

            migrationBuilder.CreateIndex(
                name: "ix_broadcast_transactions_state",
                table: "broadcast_transactions",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ix_watched_outpoints_channel_id",
                table: "watched_outpoints",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_watched_outpoints_spent_at_height",
                table: "watched_outpoints",
                column: "spent_at_height");

            // ---- Hand-written data step (not generated) ----
            // BOLT 5 plan O0-T4: every channel past funding_created that is not Closed (40) or Stale (50) gets its
            // funding output watched (purpose 1 = FundingOutput); CreatedAt is the migration time in UTC ticks. The
            // monitor runs the same backfill at every start, for channels stored later without a watch.
            migrationBuilder.Sql(
                "INSERT INTO watched_outpoints (transaction_id, output_index, channel_id, purpose, created_at) SELECT c.funding_tx_id, c.funding_output_index, c.channel_id, 1, CAST(EXTRACT(EPOCH FROM now()) * 10000000 AS bigint) + 621355968000000000 FROM channels c WHERE c.state >= 2 AND c.state NOT IN (40, 50) AND NOT EXISTS (SELECT 1 FROM watched_outpoints o WHERE o.transaction_id = c.funding_tx_id AND o.output_index = c.funding_output_index);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "block_headers");

            migrationBuilder.DropTable(
                name: "broadcast_transactions");

            migrationBuilder.DropTable(
                name: "watched_outpoints");
        }
    }
}