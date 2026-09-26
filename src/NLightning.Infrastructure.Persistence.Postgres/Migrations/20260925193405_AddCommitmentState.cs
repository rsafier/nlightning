using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddCommitmentState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "obscured_commitment_number",
                table: "htlcs");

            migrationBuilder.DropColumn(
                name: "last_revealed_per_commitment_secret",
                table: "channel_key_sets");

            migrationBuilder.RenameColumn(
                name: "signature",
                table: "htlcs",
                newName: "sha256of_onion");

            migrationBuilder.RenameColumn(
                name: "add_message_bytes",
                table: "htlcs",
                newName: "onion_routing_packet");

            migrationBuilder.AddColumn<byte[]>(
                name: "fail_reason",
                table: "htlcs",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "failure_code",
                table: "htlcs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "known_preimage",
                table: "htlcs",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "onion_shared_secret",
                table: "htlcs",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "path_key",
                table: "htlcs",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "removal_kind",
                table: "htlcs",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "data_loss_detected",
                table: "channels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<byte[]>(
                name: "error_sent",
                table: "channels",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "last_sent_order",
                table: "channels",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<byte[]>(
                name: "remote_next_per_commitment_point",
                table: "channels",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "sent_commit_diff",
                table: "channels",
                type: "bytea",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "commitments",
                columns: table => new
                {
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    slot = table.Column<byte>(type: "smallint", nullable: false),
                    number = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    feerate_per_kw = table.Column<long>(type: "bigint", nullable: false),
                    local_msat = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    remote_msat = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    htlcs = table.Column<byte[]>(type: "bytea", nullable: false),
                    per_commitment_point = table.Column<byte[]>(type: "bytea", nullable: true),
                    signature = table.Column<byte[]>(type: "bytea", nullable: true),
                    htlc_signatures = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_commitments", x => new { x.channel_id, x.slot });
                    table.ForeignKey(
                        name: "fk_commitments_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "channel_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "fee_updates",
                columns: table => new
                {
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    sequence = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    feerate_per_kw = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<byte>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fee_updates", x => new { x.channel_id, x.sequence });
                    table.ForeignKey(
                        name: "fk_fee_updates_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "channel_id",
                        onDelete: ReferentialAction.Cascade);
                });

            // ---- Hand-written data steps (not generated by EF) ----
            // NL-025: rows written before the state machine held the whole serialized update_add_htlc (type 2 + channel_id
            // 32 + id 8 + amount 8 + hash 32 + cltv 4 = 86 bytes, then the 1366-byte onion); keep only the onion (empty
            // when the row predates the mandatory onion). Such rows keep their legacy state (0-3) and the node refuses to
            // restore the channel.
            migrationBuilder.Sql(
                "UPDATE htlcs SET onion_routing_packet = CASE WHEN length(onion_routing_packet) >= 1452 THEN substring(onion_routing_packet from 87 for 1366) ELSE ''::bytea END;");

            // The legacy per-HTLC signature column was renamed by the scaffolder; its bytes are not an onion hash (HTLC
            // signatures now live on the commitment rows).
            migrationBuilder.Sql(
                "UPDATE htlcs SET sha256of_onion = NULL;");

            // NL-232: the peer's next per-commitment point of channels that already received channel_ready is the remote
            // key set's current point (its index was decremented by channel_ready).
            migrationBuilder.Sql(
                "UPDATE channels SET remote_next_per_commitment_point = (SELECT k.current_per_commitment_point FROM channel_key_sets k WHERE k.channel_id = channels.channel_id AND k.is_local = FALSE AND k.current_per_commitment_index < 281474976710655);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "commitments");

            migrationBuilder.DropTable(
                name: "fee_updates");

            migrationBuilder.DropColumn(
                name: "fail_reason",
                table: "htlcs");

            migrationBuilder.DropColumn(
                name: "failure_code",
                table: "htlcs");

            migrationBuilder.DropColumn(
                name: "known_preimage",
                table: "htlcs");

            migrationBuilder.DropColumn(
                name: "onion_shared_secret",
                table: "htlcs");

            migrationBuilder.DropColumn(
                name: "path_key",
                table: "htlcs");

            migrationBuilder.DropColumn(
                name: "removal_kind",
                table: "htlcs");

            migrationBuilder.DropColumn(
                name: "data_loss_detected",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "error_sent",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "last_sent_order",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "remote_next_per_commitment_point",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "sent_commit_diff",
                table: "channels");

            migrationBuilder.RenameColumn(
                name: "sha256of_onion",
                table: "htlcs",
                newName: "signature");

            migrationBuilder.RenameColumn(
                name: "onion_routing_packet",
                table: "htlcs",
                newName: "add_message_bytes");

            migrationBuilder.AddColumn<decimal>(
                name: "obscured_commitment_number",
                table: "htlcs",
                type: "numeric(20,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<byte[]>(
                name: "last_revealed_per_commitment_secret",
                table: "channel_key_sets",
                type: "bytea",
                nullable: true);
        }
    }
}