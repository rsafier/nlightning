using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class SplitChannelParams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "to_self_delay",
                table: "channel_configs",
                newName: "remote_to_self_delay");

            migrationBuilder.RenameColumn(
                name: "max_htlc_amount_in_flight",
                table: "channel_configs",
                newName: "remote_max_htlc_value_in_flight_msat");

            migrationBuilder.RenameColumn(
                name: "max_accepted_htlcs",
                table: "channel_configs",
                newName: "remote_max_accepted_htlcs");

            migrationBuilder.RenameColumn(
                name: "htlc_minimum_msat",
                table: "channel_configs",
                newName: "remote_htlc_minimum_msat");

            migrationBuilder.AddColumn<long>(
                name: "local_channel_reserve_amount_sats",
                table: "channel_configs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<decimal>(
                name: "local_htlc_minimum_msat",
                table: "channel_configs",
                type: "numeric(20,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "local_max_accepted_htlcs",
                table: "channel_configs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "local_max_htlc_value_in_flight_msat",
                table: "channel_configs",
                type: "numeric(20,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "local_to_self_delay",
                table: "channel_configs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "remote_channel_reserve_amount_sats",
                table: "channel_configs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Data step (NL-194): rows written before the split hold one set of values; copy it into both sides.
            // This is only approximate (migration FlagInferredChannelParams marks these rows): a non-initiator stored
            // the opener's values but announced the node's htlc_minimum_msat and dust limit in accept_channel, so its
            // Local htlc minimum is the opener's; an initiator never stored the peer's accept_channel limits, so its
            // Remote htlc minimum, max accepted, max in flight and reserve are our own values, and its open_channel
            // may have used node defaults instead of the stored ones. Old commitments were built with the one stored
            // to_self_delay, which is kept on both sides.
            migrationBuilder.Sql(
                "UPDATE channel_configs SET local_channel_reserve_amount_sats = COALESCE(channel_reserve_amount_sats, 0), remote_channel_reserve_amount_sats = COALESCE(channel_reserve_amount_sats, 0), local_htlc_minimum_msat = remote_htlc_minimum_msat, local_max_accepted_htlcs = remote_max_accepted_htlcs, local_max_htlc_value_in_flight_msat = remote_max_htlc_value_in_flight_msat, local_to_self_delay = remote_to_self_delay;");

            migrationBuilder.DropColumn(
                name: "channel_reserve_amount_sats",
                table: "channel_configs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "channel_reserve_amount_sats",
                table: "channel_configs",
                type: "bigint",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE channel_configs SET channel_reserve_amount_sats = local_channel_reserve_amount_sats;");

            migrationBuilder.DropColumn(
                name: "local_channel_reserve_amount_sats",
                table: "channel_configs");

            migrationBuilder.DropColumn(
                name: "local_htlc_minimum_msat",
                table: "channel_configs");

            migrationBuilder.DropColumn(
                name: "local_max_accepted_htlcs",
                table: "channel_configs");

            migrationBuilder.DropColumn(
                name: "local_max_htlc_value_in_flight_msat",
                table: "channel_configs");

            migrationBuilder.DropColumn(
                name: "local_to_self_delay",
                table: "channel_configs");

            migrationBuilder.DropColumn(
                name: "remote_channel_reserve_amount_sats",
                table: "channel_configs");

            migrationBuilder.RenameColumn(
                name: "remote_to_self_delay",
                table: "channel_configs",
                newName: "to_self_delay");

            migrationBuilder.RenameColumn(
                name: "remote_max_htlc_value_in_flight_msat",
                table: "channel_configs",
                newName: "max_htlc_amount_in_flight");

            migrationBuilder.RenameColumn(
                name: "remote_max_accepted_htlcs",
                table: "channel_configs",
                newName: "max_accepted_htlcs");

            migrationBuilder.RenameColumn(
                name: "remote_htlc_minimum_msat",
                table: "channel_configs",
                newName: "htlc_minimum_msat");
        }
    }
}