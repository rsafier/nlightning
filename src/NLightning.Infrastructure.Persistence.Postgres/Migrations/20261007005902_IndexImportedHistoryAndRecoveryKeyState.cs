using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class IndexImportedHistoryAndRecoveryKeyState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "funding_keys_unknown",
                table: "channel_fundings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Older recovery rows may contain copied keys whose aggregate never matched the funding output.
            // Conservatively mark their hints unknown; payment-basepoint recovery remains available.
            migrationBuilder.Sql("UPDATE channel_fundings SET funding_keys_unknown = TRUE WHERE channel_id IN (SELECT c.channel_id FROM channels c JOIN channel_configs cc ON cc.channel_id = c.channel_id WHERE c.data_loss_detected = TRUE AND cc.option_simple_taproot = TRUE AND c.last_received_signature IS NULL AND c.last_sent_signature IS NULL)");

            migrationBuilder.CreateTable(
                name: "ImportedWatchIndexes",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<long>(type: "bigint", nullable: false),
                    block_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    script_set = table.Column<string>(type: "text", nullable: false),
                    history = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_imported_watch_indexes", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportedWatchIndexes");

            migrationBuilder.DropColumn(
                name: "funding_keys_unknown",
                table: "channel_fundings");
        }
    }
}