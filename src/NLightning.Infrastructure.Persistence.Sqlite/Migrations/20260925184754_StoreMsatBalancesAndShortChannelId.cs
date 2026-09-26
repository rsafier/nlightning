using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class StoreMsatBalancesAndShortChannelId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LocalBalanceMsat",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RemoteBalanceMsat",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<byte[]>(
                name: "ShortChannelId",
                table: "Channels",
                type: "BLOB",
                nullable: true);

            // Data step (NL-191): balances were whole satoshis; store them in msat. The msat part that was lost
            // before this migration cannot be recovered.
            migrationBuilder.Sql(
                "UPDATE \"Channels\" SET \"LocalBalanceMsat\" = CAST(\"LocalBalanceSatoshis\" * 1000 AS INTEGER), \"RemoteBalanceMsat\" = CAST(\"RemoteBalanceSatoshis\" * 1000 AS INTEGER);");

            migrationBuilder.DropColumn(
                name: "LocalBalanceSatoshis",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "RemoteBalanceSatoshis",
                table: "Channels");

            // Data step (NL-190): channels created before HTLC ids started at 0 have next ids 1. None of them ever
            // carried an HTLC, so reset both ids to 0 where the channel has no HTLC row.
            migrationBuilder.Sql(
                "UPDATE \"Channels\" SET \"LocalNextHtlcId\" = 0, \"RemoteNextHtlcId\" = 0 WHERE \"LocalNextHtlcId\" = 1 AND \"RemoteNextHtlcId\" = 1 AND NOT EXISTS (SELECT 1 FROM \"Htlcs\" WHERE \"Htlcs\".\"ChannelId\" = \"Channels\".\"ChannelId\");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "LocalBalanceSatoshis",
                table: "Channels",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RemoteBalanceSatoshis",
                table: "Channels",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.Sql(
                "UPDATE \"Channels\" SET \"LocalBalanceSatoshis\" = \"LocalBalanceMsat\" / 1000, \"RemoteBalanceSatoshis\" = \"RemoteBalanceMsat\" / 1000;");

            migrationBuilder.DropColumn(
                name: "LocalBalanceMsat",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "RemoteBalanceMsat",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "ShortChannelId",
                table: "Channels");
        }
    }
}