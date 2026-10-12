using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddPassiveHtlcCheckpointsAndWalletHistoryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reverses_event_key",
                table: "accounting_events",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // Backfill the derived query reference without changing sealed event payloads or hashes.
            migrationBuilder.Sql("""
                UPDATE "accounting_events" SET "reverses_event_key" = COALESCE(
                    NULLIF("details"::jsonb ->> 'reverses', ''),
                    NULLIF(substring("event_key" from '^(.*):rev:'), '')
                ) WHERE "kind" = 60;
                """);

            migrationBuilder.CreateTable(
                name: "onchain_htlc_observations",
                columns: table => new
                {
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    direction = table.Column<byte>(type: "smallint", nullable: false),
                    htlc_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    settled = table.Column<bool>(type: "boolean", nullable: false),
                    observed_at = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_onchain_htlc_observations", x => new { x.channel_id, x.direction, x.htlc_id, x.settled });
                });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_events_kind_block_height_ledger_seq",
                table: "accounting_events",
                columns: new[] { "kind", "block_height", "ledger_seq" });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_events_reverses_event_key",
                table: "accounting_events",
                column: "reverses_event_key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "onchain_htlc_observations");

            migrationBuilder.DropIndex(
                name: "ix_accounting_events_kind_block_height_ledger_seq",
                table: "accounting_events");

            migrationBuilder.DropIndex(
                name: "ix_accounting_events_reverses_event_key",
                table: "accounting_events");

            migrationBuilder.DropColumn(
                name: "reverses_event_key",
                table: "accounting_events");
        }
    }
}