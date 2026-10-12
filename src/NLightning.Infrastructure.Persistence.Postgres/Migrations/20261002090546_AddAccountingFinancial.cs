using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountingFinancial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_accounting_postings_accounting_entries_ledger_seq",
                table: "accounting_postings");

            migrationBuilder.DropPrimaryKey(
                name: "pk_accounting_postings",
                table: "accounting_postings");

            migrationBuilder.DropIndex(
                name: "ix_accounting_postings_account_occurred_at",
                table: "accounting_postings");

            migrationBuilder.DropPrimaryKey(
                name: "pk_accounting_entries",
                table: "accounting_entries");

            migrationBuilder.DropIndex(
                name: "ix_accounting_entries_event_key",
                table: "accounting_entries");

            migrationBuilder.DropIndex(
                name: "ix_accounting_entries_occurred_at",
                table: "accounting_entries");

            migrationBuilder.DropPrimaryKey(
                name: "pk_accounting_cursor",
                table: "accounting_cursor");

            migrationBuilder.DropPrimaryKey(
                name: "pk_accounting_balances",
                table: "accounting_balances");

            migrationBuilder.DropColumn(
                name: "id",
                table: "accounting_cursor");

            migrationBuilder.AddColumn<string>(
                name: "label",
                table: "payments",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tags",
                table: "payments",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "label",
                table: "offers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tags",
                table: "offers",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "label",
                table: "invoices",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tags",
                table: "invoices",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "label",
                table: "channels",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tags",
                table: "channels",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "label",
                table: "broadcast_transactions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tags",
                table: "broadcast_transactions",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "book",
                table: "accounting_postings",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<int>(
                name: "adjustment",
                table: "accounting_postings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "account_name",
                table: "accounting_postings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "fiat_amount",
                table: "accounting_postings",
                type: "numeric(28,8)",
                precision: 28,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fiat_currency",
                table: "accounting_postings",
                type: "character(3)",
                fixedLength: true,
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "price_id",
                table: "accounting_postings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "book",
                table: "accounting_entries",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<int>(
                name: "adjustment",
                table: "accounting_entries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<byte>(
                name: "classification",
                table: "accounting_entries",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "closed_period_id",
                table: "accounting_entries",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "flags",
                table: "accounting_entries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "rule_id",
                table: "accounting_entries",
                type: "bigint",
                nullable: true);

            // The A2 singleton row (Id 1) becomes the operational book's cursor (Book 0) through the column default
            migrationBuilder.AddColumn<byte>(
                name: "book",
                table: "accounting_cursor",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<byte>(
                name: "book",
                table: "accounting_balances",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<string>(
                name: "account_name",
                table: "accounting_balances",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "fiat_amount",
                table: "accounting_balances",
                type: "numeric(28,8)",
                precision: 28,
                scale: 8,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddPrimaryKey(
                name: "pk_accounting_postings",
                table: "accounting_postings",
                columns: new[] { "book", "ledger_seq", "adjustment", "index" });

            migrationBuilder.AddPrimaryKey(
                name: "pk_accounting_entries",
                table: "accounting_entries",
                columns: new[] { "book", "ledger_seq", "adjustment" });

            migrationBuilder.AddPrimaryKey(
                name: "pk_accounting_cursor",
                table: "accounting_cursor",
                column: "book");

            migrationBuilder.AddPrimaryKey(
                name: "pk_accounting_balances",
                table: "accounting_balances",
                columns: new[] { "book", "account", "account_name" });

            migrationBuilder.CreateTable(
                name: "accounting_overrides",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    event_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    account = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    note = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    created_at = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_overrides", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "accounting_periods",
                columns: table => new
                {
                    period_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    start = table.Column<long>(type: "bigint", nullable: false),
                    end = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<byte>(type: "smallint", nullable: false),
                    closed_at = table.Column<long>(type: "bigint", nullable: true),
                    last_ledger_seq = table.Column<long>(type: "bigint", nullable: false),
                    chain_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    digest = table.Column<byte[]>(type: "bytea", nullable: true),
                    signature = table.Column<byte[]>(type: "bytea", nullable: true),
                    forced = table.Column<bool>(type: "boolean", nullable: false),
                    closing_state = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_periods", x => x.period_id);
                });

            migrationBuilder.CreateTable(
                name: "accounting_prices",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    time = table.Column<long>(type: "bigint", nullable: false),
                    price = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    source = table.Column<byte>(type: "smallint", nullable: false),
                    fetched_at = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_prices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "accounting_rules",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    kinds = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    label_pattern = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    tag_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    tag_value = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    counterparty = table.Column<byte[]>(type: "bytea", nullable: true),
                    offer_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    target_account = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                    description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "accounting_lots",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    acquired_at = table.Column<long>(type: "bigint", nullable: false),
                    origin = table.Column<byte>(type: "smallint", nullable: false),
                    source_ledger_seq = table.Column<long>(type: "bigint", nullable: true),
                    source_adjustment = table.Column<int>(type: "integer", nullable: false),
                    account = table.Column<int>(type: "integer", nullable: true),
                    parent_lot_id = table.Column<long>(type: "bigint", nullable: true),
                    original_msat = table.Column<long>(type: "bigint", nullable: false),
                    remaining_msat = table.Column<long>(type: "bigint", nullable: false),
                    fiat_cost = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: true),
                    fiat_currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: true),
                    price_id = table.Column<long>(type: "bigint", nullable: true),
                    basis_estimated = table.Column<bool>(type: "boolean", nullable: false),
                    closed_period_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_lots", x => x.id);
                    table.ForeignKey(
                        name: "fk_accounting_lots_accounting_prices_price_id",
                        column: x => x.price_id,
                        principalTable: "accounting_prices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "accounting_lot_reliefs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    lot_id = table.Column<long>(type: "bigint", nullable: false),
                    ledger_seq = table.Column<long>(type: "bigint", nullable: false),
                    adjustment = table.Column<int>(type: "integer", nullable: false),
                    relieved_at = table.Column<long>(type: "bigint", nullable: false),
                    msat = table.Column<long>(type: "bigint", nullable: false),
                    fiat_cost_relieved = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: true),
                    proceeds = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: true),
                    closed_period_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_lot_reliefs", x => x.id);
                    table.ForeignKey(
                        name: "fk_accounting_lot_reliefs_accounting_lots_lot_id",
                        column: x => x.lot_id,
                        principalTable: "accounting_lots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_postings_book_account_occurred_at",
                table: "accounting_postings",
                columns: new[] { "book", "account", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_postings_price_id_book_occurred_at",
                table: "accounting_postings",
                columns: new[] { "price_id", "book", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_entries_book_closed_period_id",
                table: "accounting_entries",
                columns: new[] { "book", "closed_period_id" });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_entries_book_event_key_adjustment",
                table: "accounting_entries",
                columns: new[] { "book", "event_key", "adjustment" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_accounting_entries_book_occurred_at",
                table: "accounting_entries",
                columns: new[] { "book", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_lot_reliefs_closed_period_id",
                table: "accounting_lot_reliefs",
                column: "closed_period_id");

            migrationBuilder.CreateIndex(
                name: "ix_accounting_lot_reliefs_ledger_seq_adjustment",
                table: "accounting_lot_reliefs",
                columns: new[] { "ledger_seq", "adjustment" });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_lot_reliefs_lot_id",
                table: "accounting_lot_reliefs",
                column: "lot_id");

            migrationBuilder.CreateIndex(
                name: "ix_accounting_lot_reliefs_relieved_at",
                table: "accounting_lot_reliefs",
                column: "relieved_at");

            migrationBuilder.CreateIndex(
                name: "ix_accounting_lots_closed_period_id",
                table: "accounting_lots",
                column: "closed_period_id");

            migrationBuilder.CreateIndex(
                name: "ix_accounting_lots_price_id",
                table: "accounting_lots",
                column: "price_id");

            migrationBuilder.CreateIndex(
                name: "ix_accounting_lots_remaining_msat_acquired_at",
                table: "accounting_lots",
                columns: new[] { "remaining_msat", "acquired_at" });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_lots_source_ledger_seq_source_adjustment",
                table: "accounting_lots",
                columns: new[] { "source_ledger_seq", "source_adjustment" });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_overrides_event_key",
                table: "accounting_overrides",
                column: "event_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_accounting_periods_state_end",
                table: "accounting_periods",
                columns: new[] { "state", "end" });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_prices_currency_time",
                table: "accounting_prices",
                columns: new[] { "currency", "time" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_accounting_rules_priority_id",
                table: "accounting_rules",
                columns: new[] { "priority", "id" });

            migrationBuilder.AddForeignKey(
                name: "fk_accounting_postings_accounting_entries_book_ledger_seq_adju",
                table: "accounting_postings",
                columns: new[] { "book", "ledger_seq", "adjustment" },
                principalTable: "accounting_entries",
                principalColumns: new[] { "book", "ledger_seq", "adjustment" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_accounting_postings_accounting_prices_price_id",
                table: "accounting_postings",
                column: "price_id",
                principalTable: "accounting_prices",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The older schema holds the operational book only: the financial book's rows go first (they would
            // collide on the older keys)
            migrationBuilder.Sql("DELETE FROM accounting_postings WHERE book <> 0;");
            migrationBuilder.Sql("DELETE FROM accounting_entries WHERE book <> 0;");
            migrationBuilder.Sql("DELETE FROM accounting_balances WHERE book <> 0;");
            migrationBuilder.Sql("DELETE FROM accounting_cursor WHERE book <> 0;");

            migrationBuilder.DropForeignKey(
                name: "fk_accounting_postings_accounting_entries_book_ledger_seq_adju",
                table: "accounting_postings");

            migrationBuilder.DropForeignKey(
                name: "fk_accounting_postings_accounting_prices_price_id",
                table: "accounting_postings");

            migrationBuilder.DropTable(
                name: "accounting_lot_reliefs");

            migrationBuilder.DropTable(
                name: "accounting_overrides");

            migrationBuilder.DropTable(
                name: "accounting_periods");

            migrationBuilder.DropTable(
                name: "accounting_rules");

            migrationBuilder.DropTable(
                name: "accounting_lots");

            migrationBuilder.DropTable(
                name: "accounting_prices");

            migrationBuilder.DropPrimaryKey(
                name: "pk_accounting_postings",
                table: "accounting_postings");

            migrationBuilder.DropIndex(
                name: "ix_accounting_postings_book_account_occurred_at",
                table: "accounting_postings");

            migrationBuilder.DropIndex(
                name: "ix_accounting_postings_price_id_book_occurred_at",
                table: "accounting_postings");

            migrationBuilder.DropPrimaryKey(
                name: "pk_accounting_entries",
                table: "accounting_entries");

            migrationBuilder.DropIndex(
                name: "ix_accounting_entries_book_closed_period_id",
                table: "accounting_entries");

            migrationBuilder.DropIndex(
                name: "ix_accounting_entries_book_event_key_adjustment",
                table: "accounting_entries");

            migrationBuilder.DropIndex(
                name: "ix_accounting_entries_book_occurred_at",
                table: "accounting_entries");

            migrationBuilder.DropPrimaryKey(
                name: "pk_accounting_cursor",
                table: "accounting_cursor");

            migrationBuilder.DropPrimaryKey(
                name: "pk_accounting_balances",
                table: "accounting_balances");

            migrationBuilder.DropColumn(
                name: "label",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "tags",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "label",
                table: "offers");

            migrationBuilder.DropColumn(
                name: "tags",
                table: "offers");

            migrationBuilder.DropColumn(
                name: "label",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "tags",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "label",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "tags",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "label",
                table: "broadcast_transactions");

            migrationBuilder.DropColumn(
                name: "tags",
                table: "broadcast_transactions");

            migrationBuilder.DropColumn(
                name: "book",
                table: "accounting_postings");

            migrationBuilder.DropColumn(
                name: "adjustment",
                table: "accounting_postings");

            migrationBuilder.DropColumn(
                name: "account_name",
                table: "accounting_postings");

            migrationBuilder.DropColumn(
                name: "fiat_amount",
                table: "accounting_postings");

            migrationBuilder.DropColumn(
                name: "fiat_currency",
                table: "accounting_postings");

            migrationBuilder.DropColumn(
                name: "price_id",
                table: "accounting_postings");

            migrationBuilder.DropColumn(
                name: "book",
                table: "accounting_entries");

            migrationBuilder.DropColumn(
                name: "adjustment",
                table: "accounting_entries");

            migrationBuilder.DropColumn(
                name: "classification",
                table: "accounting_entries");

            migrationBuilder.DropColumn(
                name: "closed_period_id",
                table: "accounting_entries");

            migrationBuilder.DropColumn(
                name: "flags",
                table: "accounting_entries");

            migrationBuilder.DropColumn(
                name: "rule_id",
                table: "accounting_entries");

            migrationBuilder.DropColumn(
                name: "book",
                table: "accounting_cursor");

            migrationBuilder.DropColumn(
                name: "book",
                table: "accounting_balances");

            migrationBuilder.DropColumn(
                name: "account_name",
                table: "accounting_balances");

            migrationBuilder.DropColumn(
                name: "fiat_amount",
                table: "accounting_balances");

            migrationBuilder.AddColumn<int>(
                name: "id",
                table: "accounting_cursor",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // The operational cursor row is the A2 singleton again (Id 1)
            migrationBuilder.Sql("UPDATE accounting_cursor SET id = 1;");

            migrationBuilder.AddPrimaryKey(
                name: "pk_accounting_postings",
                table: "accounting_postings",
                columns: new[] { "ledger_seq", "index" });

            migrationBuilder.AddPrimaryKey(
                name: "pk_accounting_entries",
                table: "accounting_entries",
                column: "ledger_seq");

            migrationBuilder.AddPrimaryKey(
                name: "pk_accounting_cursor",
                table: "accounting_cursor",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_accounting_balances",
                table: "accounting_balances",
                column: "account");

            migrationBuilder.CreateIndex(
                name: "ix_accounting_postings_account_occurred_at",
                table: "accounting_postings",
                columns: new[] { "account", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_entries_event_key",
                table: "accounting_entries",
                column: "event_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_accounting_entries_occurred_at",
                table: "accounting_entries",
                column: "occurred_at");

            migrationBuilder.AddForeignKey(
                name: "fk_accounting_postings_accounting_entries_ledger_seq",
                table: "accounting_postings",
                column: "ledger_seq",
                principalTable: "accounting_entries",
                principalColumn: "ledger_seq",
                onDelete: ReferentialAction.Cascade);
        }
    }
}