using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountingEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "push_amount_msat",
                table: "channels",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "fee_sat",
                table: "broadcast_transactions",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "accounting_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    event_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<long>(type: "bigint", nullable: false),
                    block_height = table.Column<long>(type: "bigint", nullable: true),
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    short_channel_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    payment_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    tx_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    output_index = table.Column<long>(type: "bigint", nullable: true),
                    counterparty = table.Column<byte[]>(type: "bytea", nullable: true),
                    amount_msat = table.Column<long>(type: "bigint", nullable: false),
                    fee_msat = table.Column<long>(type: "bigint", nullable: false),
                    finality = table.Column<byte>(type: "smallint", nullable: false),
                    flags = table.Column<int>(type: "integer", nullable: false),
                    details = table.Column<string>(type: "text", nullable: true),
                    ledger_seq = table.Column<long>(type: "bigint", nullable: true),
                    hash = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_events_channel_id",
                table: "accounting_events",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_accounting_events_event_key",
                table: "accounting_events",
                column: "event_key");

            migrationBuilder.CreateIndex(
                name: "ix_accounting_events_ledger_seq",
                table: "accounting_events",
                column: "ledger_seq");

            migrationBuilder.CreateIndex(
                name: "ix_accounting_events_occurred_at",
                table: "accounting_events",
                column: "occurred_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "accounting_events");

            migrationBuilder.DropColumn(
                name: "push_amount_msat",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "fee_sat",
                table: "broadcast_transactions");
        }
    }
}