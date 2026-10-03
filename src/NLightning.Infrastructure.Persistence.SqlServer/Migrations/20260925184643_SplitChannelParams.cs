using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class SplitChannelParams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ToSelfDelay",
                table: "ChannelConfigs",
                newName: "RemoteToSelfDelay");

            migrationBuilder.RenameColumn(
                name: "MaxHtlcAmountInFlight",
                table: "ChannelConfigs",
                newName: "RemoteMaxHtlcValueInFlightMsat");

            migrationBuilder.RenameColumn(
                name: "MaxAcceptedHtlcs",
                table: "ChannelConfigs",
                newName: "RemoteMaxAcceptedHtlcs");

            migrationBuilder.RenameColumn(
                name: "HtlcMinimumMsat",
                table: "ChannelConfigs",
                newName: "RemoteHtlcMinimumMsat");

            migrationBuilder.AddColumn<long>(
                name: "LocalChannelReserveAmountSats",
                table: "ChannelConfigs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<decimal>(
                name: "LocalHtlcMinimumMsat",
                table: "ChannelConfigs",
                type: "decimal(20,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "LocalMaxAcceptedHtlcs",
                table: "ChannelConfigs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "LocalMaxHtlcValueInFlightMsat",
                table: "ChannelConfigs",
                type: "decimal(20,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "LocalToSelfDelay",
                table: "ChannelConfigs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "RemoteChannelReserveAmountSats",
                table: "ChannelConfigs",
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
                "UPDATE [ChannelConfigs] SET [LocalChannelReserveAmountSats] = COALESCE([ChannelReserveAmountSats], 0), [RemoteChannelReserveAmountSats] = COALESCE([ChannelReserveAmountSats], 0), [LocalHtlcMinimumMsat] = [RemoteHtlcMinimumMsat], [LocalMaxAcceptedHtlcs] = [RemoteMaxAcceptedHtlcs], [LocalMaxHtlcValueInFlightMsat] = [RemoteMaxHtlcValueInFlightMsat], [LocalToSelfDelay] = [RemoteToSelfDelay];");

            migrationBuilder.DropColumn(
                name: "ChannelReserveAmountSats",
                table: "ChannelConfigs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ChannelReserveAmountSats",
                table: "ChannelConfigs",
                type: "bigint",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE [ChannelConfigs] SET [ChannelReserveAmountSats] = [LocalChannelReserveAmountSats];");

            migrationBuilder.DropColumn(
                name: "LocalChannelReserveAmountSats",
                table: "ChannelConfigs");

            migrationBuilder.DropColumn(
                name: "LocalHtlcMinimumMsat",
                table: "ChannelConfigs");

            migrationBuilder.DropColumn(
                name: "LocalMaxAcceptedHtlcs",
                table: "ChannelConfigs");

            migrationBuilder.DropColumn(
                name: "LocalMaxHtlcValueInFlightMsat",
                table: "ChannelConfigs");

            migrationBuilder.DropColumn(
                name: "LocalToSelfDelay",
                table: "ChannelConfigs");

            migrationBuilder.DropColumn(
                name: "RemoteChannelReserveAmountSats",
                table: "ChannelConfigs");

            migrationBuilder.RenameColumn(
                name: "RemoteToSelfDelay",
                table: "ChannelConfigs",
                newName: "ToSelfDelay");

            migrationBuilder.RenameColumn(
                name: "RemoteMaxHtlcValueInFlightMsat",
                table: "ChannelConfigs",
                newName: "MaxHtlcAmountInFlight");

            migrationBuilder.RenameColumn(
                name: "RemoteMaxAcceptedHtlcs",
                table: "ChannelConfigs",
                newName: "MaxAcceptedHtlcs");

            migrationBuilder.RenameColumn(
                name: "RemoteHtlcMinimumMsat",
                table: "ChannelConfigs",
                newName: "HtlcMinimumMsat");
        }
    }
}