using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddDanglingWalletHistoryAccountsAndPriceAudits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ownership_summary",
                table: "wallet_transactions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "account_index",
                table: "wallet_addresses",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "account_name",
                table: "wallet_addresses",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "default");

            migrationBuilder.AddColumn<long>(
                name: "derivation_index",
                table: "wallet_addresses",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "actual_incoming_amount_msat",
                table: "forward_circuits",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "incoming_claimed_preimage",
                table: "forward_circuits",
                type: "bytea",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "accounting_price_replacement_audits",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    price_id = table.Column<long>(type: "bigint", nullable: false),
                    old_price = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    new_price = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    old_source = table.Column<byte>(type: "smallint", nullable: false),
                    new_source = table.Column<byte>(type: "smallint", nullable: false),
                    old_fetched_at = table.Column<long>(type: "bigint", nullable: false),
                    replaced_at = table.Column<long>(type: "bigint", nullable: false),
                    operator_source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_price_replacement_audits", x => x.id);
                    table.ForeignKey(
                        name: "fk_accounting_price_replacement_audits_accounting_prices_price",
                        column: x => x.price_id,
                        principalTable: "accounting_prices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wallet_accounts",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    address_type = table.Column<byte>(type: "smallint", nullable: false),
                    account_index = table.Column<long>(type: "bigint", nullable: false),
                    extended_public_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    master_fingerprint = table.Column<byte[]>(type: "bytea", nullable: false),
                    derivation_path = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    watch_only = table.Column<bool>(type: "boolean", nullable: false),
                    birthday_height = table.Column<long>(type: "bigint", nullable: false),
                    external_key_count = table.Column<long>(type: "bigint", nullable: false),
                    internal_key_count = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallet_accounts", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "wallet_history_rescan_states",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    generation = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_from_height = table.Column<long>(type: "bigint", nullable: false),
                    available_from_height = table.Column<long>(type: "bigint", nullable: false),
                    target_height = table.Column<long>(type: "bigint", nullable: false),
                    cursor_height = table.Column<long>(type: "bigint", nullable: true),
                    cursor_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: true),
                    address_count = table.Column<long>(type: "bigint", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_partial = table.Column<bool>(type: "boolean", nullable: false),
                    error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallet_history_rescan_states", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "wallet_transaction_labels",
                columns: table => new
                {
                    transaction_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    label = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallet_transaction_labels", x => x.transaction_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_price_replacement_audits_price_id_replaced_at_id",
                table: "accounting_price_replacement_audits",
                columns: new[] { "price_id", "replaced_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "accounting_price_replacement_audits");

            migrationBuilder.DropTable(
                name: "wallet_accounts");

            migrationBuilder.DropTable(
                name: "wallet_history_rescan_states");

            migrationBuilder.DropTable(
                name: "wallet_transaction_labels");

            migrationBuilder.DropColumn(
                name: "ownership_summary",
                table: "wallet_transactions");

            migrationBuilder.DropColumn(
                name: "account_index",
                table: "wallet_addresses");

            migrationBuilder.DropColumn(
                name: "account_name",
                table: "wallet_addresses");

            migrationBuilder.DropColumn(
                name: "derivation_index",
                table: "wallet_addresses");

            migrationBuilder.DropColumn(
                name: "actual_incoming_amount_msat",
                table: "forward_circuits");

            migrationBuilder.DropColumn(
                name: "incoming_claimed_preimage",
                table: "forward_circuits");
        }
    }
}