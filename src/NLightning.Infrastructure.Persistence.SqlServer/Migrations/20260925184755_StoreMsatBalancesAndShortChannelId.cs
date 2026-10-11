using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class StoreMsatBalancesAndShortChannelId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "RemoteBalanceSatoshis",
                table: "Channels",
                newName: "RemoteBalanceMsat");

            migrationBuilder.RenameColumn(
                name: "LocalBalanceSatoshis",
                table: "Channels",
                newName: "LocalBalanceMsat");

            migrationBuilder.AddColumn<byte[]>(
                name: "ShortChannelId",
                table: "Channels",
                type: "varbinary(8)",
                nullable: true);

            // Data step (NL-191): balances were whole satoshis; store them in msat. The msat part that was lost
            // before this migration cannot be recovered.
            migrationBuilder.Sql(
                "UPDATE [Channels] SET [LocalBalanceMsat] = [LocalBalanceMsat] * 1000, [RemoteBalanceMsat] = [RemoteBalanceMsat] * 1000;");

            // Data step (NL-190): channels created before HTLC ids started at 0 have next ids 1. None of them ever
            // carried an HTLC, so reset both ids to 0 where the channel has no HTLC row.
            migrationBuilder.Sql(
                "UPDATE [Channels] SET [LocalNextHtlcId] = 0, [RemoteNextHtlcId] = 0 WHERE [LocalNextHtlcId] = 1 AND [RemoteNextHtlcId] = 1 AND NOT EXISTS (SELECT 1 FROM [Htlcs] WHERE [Htlcs].[ChannelId] = [Channels].[ChannelId]);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE [Channels] SET [LocalBalanceMsat] = [LocalBalanceMsat] / 1000, [RemoteBalanceMsat] = [RemoteBalanceMsat] / 1000;");

            migrationBuilder.DropColumn(
                name: "ShortChannelId",
                table: "Channels");

            migrationBuilder.RenameColumn(
                name: "RemoteBalanceMsat",
                table: "Channels",
                newName: "RemoteBalanceSatoshis");

            migrationBuilder.RenameColumn(
                name: "LocalBalanceMsat",
                table: "Channels",
                newName: "LocalBalanceSatoshis");
        }
    }
}