using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSpliceFundings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_revoked_commitments",
                table: "revoked_commitments");

            migrationBuilder.DropPrimaryKey(
                name: "pk_commitments",
                table: "commitments");

            migrationBuilder.AddColumn<byte[]>(
                name: "funding_tx_id",
                table: "revoked_commitments",
                type: "bytea",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "funding_tx_id",
                table: "commitments",
                type: "bytea",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<bool>(
                name: "is_dual_funded",
                table: "channels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "local_funding_contribution_satoshis",
                table: "channels",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "remote_funding_contribution_satoshis",
                table: "channels",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "pk_revoked_commitments",
                table: "revoked_commitments",
                columns: new[] { "channel_id", "number", "funding_tx_id" });

            migrationBuilder.AddPrimaryKey(
                name: "pk_commitments",
                table: "commitments",
                columns: new[] { "channel_id", "slot", "funding_tx_id" });

            migrationBuilder.CreateTable(
                name: "channel_fundings",
                columns: table => new
                {
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    funding_tx_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    output_index = table.Column<int>(type: "integer", nullable: false),
                    capacity_satoshis = table.Column<long>(type: "bigint", nullable: false),
                    local_funding_pub_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    remote_funding_pub_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    local_funding_key_index = table.Column<long>(type: "bigint", nullable: false),
                    local_balance_delta_msat = table.Column<long>(type: "bigint", nullable: false),
                    remote_balance_delta_msat = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<byte>(type: "smallint", nullable: false),
                    status = table.Column<byte>(type: "smallint", nullable: false),
                    feerate_per_kw = table.Column<long>(type: "bigint", nullable: true),
                    locktime = table.Column<long>(type: "bigint", nullable: true),
                    rbf_of = table.Column<byte[]>(type: "bytea", nullable: true),
                    confirmed_height = table.Column<long>(type: "bigint", nullable: true),
                    short_channel_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    splice_locked_sent = table.Column<bool>(type: "boolean", nullable: false),
                    splice_locked_received = table.Column<bool>(type: "boolean", nullable: false),
                    announcement_signatures_received = table.Column<bool>(type: "boolean", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_fundings", x => new { x.channel_id, x.funding_tx_id });
                    table.ForeignKey(
                        name: "fk_channel_fundings_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "channel_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "channel_policies",
                columns: table => new
                {
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    fee_base_msat = table.Column<long>(type: "bigint", nullable: true),
                    fee_proportional_millionths = table.Column<long>(type: "bigint", nullable: true),
                    cltv_expiry_delta = table.Column<int>(type: "integer", nullable: true),
                    htlc_minimum_msat = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    htlc_maximum_msat = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    updated_at = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_policies", x => x.channel_id);
                });

            // ---- Hand-written data step (splicing plan §3.8, lane SP1-C): not generated by EF ----
            // Existing commitment rows and revocation-log rows spend the channel's current funding: fill their new
            // FundingTxId key column from Channels.FundingTxId. Then give every channel with a known funding outpoint
            // its Current, Initial ChannelFundings row (funding key index 0, no deltas, both funding keys from the key
            // sets, the confirmed short channel id if any).
            migrationBuilder.Sql("UPDATE commitments AS m SET funding_tx_id = c.funding_tx_id FROM channels AS c WHERE c.channel_id = m.channel_id;");
            migrationBuilder.Sql("UPDATE revoked_commitments AS m SET funding_tx_id = c.funding_tx_id FROM channels AS c WHERE c.channel_id = m.channel_id;");
            migrationBuilder.Sql("INSERT INTO channel_fundings (channel_id, funding_tx_id, output_index, capacity_satoshis, local_funding_pub_key, remote_funding_pub_key, local_funding_key_index, local_balance_delta_msat, remote_balance_delta_msat, kind, status, short_channel_id, splice_locked_sent, splice_locked_received, announcement_signatures_received, sequence) SELECT c.channel_id, c.funding_tx_id, c.funding_output_index, c.funding_amount_satoshis, l.funding_pub_key, r.funding_pub_key, 0, 0, 0, 0, 1, c.short_channel_id, FALSE, FALSE, FALSE, 0 FROM channels AS c INNER JOIN channel_key_sets AS l ON l.channel_id = c.channel_id AND l.is_local INNER JOIN channel_key_sets AS r ON r.channel_id = c.channel_id AND NOT r.is_local WHERE c.funding_tx_id <> decode(repeat('00', 32), 'hex');");
            // ---- End of the hand-written data step ----
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ---- Hand-written guard: refuse while a channel runs on a locked splice. Its rotated funding keys live
            // only in the ChannelFundings row (the key sets keep the initial keys), so a build without that table would
            // rebuild the funding script and sign with the wrong key ----
            migrationBuilder.Sql("DO $$ BEGIN IF EXISTS (SELECT 1 FROM channel_fundings WHERE status = 1 AND kind <> 0) THEN RAISE EXCEPTION 'AddSpliceFundings Down refused: a channel runs on a locked splice whose funding keys only channel_fundings holds'; END IF; END $$;");
            // ---- End of the hand-written guard ----

            // ---- Hand-written data step: before the old keys come back, keep only the rows of the current funding ----
            migrationBuilder.Sql("DELETE FROM commitments AS m USING channels AS c WHERE c.channel_id = m.channel_id AND m.funding_tx_id <> c.funding_tx_id;");
            migrationBuilder.Sql("DELETE FROM revoked_commitments AS m USING channels AS c WHERE c.channel_id = m.channel_id AND m.funding_tx_id <> c.funding_tx_id;");
            // ---- End of the hand-written data step ----

            migrationBuilder.DropTable(
                name: "channel_fundings");

            migrationBuilder.DropTable(
                name: "channel_policies");

            migrationBuilder.DropPrimaryKey(
                name: "pk_revoked_commitments",
                table: "revoked_commitments");

            migrationBuilder.DropPrimaryKey(
                name: "pk_commitments",
                table: "commitments");

            migrationBuilder.DropColumn(
                name: "funding_tx_id",
                table: "revoked_commitments");

            migrationBuilder.DropColumn(
                name: "funding_tx_id",
                table: "commitments");

            migrationBuilder.DropColumn(
                name: "is_dual_funded",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "local_funding_contribution_satoshis",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "remote_funding_contribution_satoshis",
                table: "channels");

            migrationBuilder.AddPrimaryKey(
                name: "pk_revoked_commitments",
                table: "revoked_commitments",
                columns: new[] { "channel_id", "number" });

            migrationBuilder.AddPrimaryKey(
                name: "pk_commitments",
                table: "commitments",
                columns: new[] { "channel_id", "slot" });
        }
    }
}