using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class StoreMsatBalancesAndShortChannelId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "local_balance_msat",
                table: "channels",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "remote_balance_msat",
                table: "channels",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<byte[]>(
                name: "short_channel_id",
                table: "channels",
                type: "bytea",
                nullable: true);

            // Data step (NL-191): balances were whole satoshis; store them in msat. The msat part that was lost
            // before this migration cannot be recovered.
            migrationBuilder.Sql(
                "UPDATE channels SET local_balance_msat = CAST(local_balance_satoshis * 1000 AS bigint), remote_balance_msat = CAST(remote_balance_satoshis * 1000 AS bigint);");

            migrationBuilder.DropColumn(
                name: "local_balance_satoshis",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "remote_balance_satoshis",
                table: "channels");

            // Data step (NL-190): channels created before HTLC ids started at 0 have next ids 1. None of them ever
            // carried an HTLC, so reset both ids to 0 where the channel has no HTLC row.
            migrationBuilder.Sql(
                "UPDATE channels SET local_next_htlc_id = 0, remote_next_htlc_id = 0 WHERE local_next_htlc_id = 1 AND remote_next_htlc_id = 1 AND NOT EXISTS (SELECT 1 FROM htlcs WHERE htlcs.channel_id = channels.channel_id);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "local_balance_satoshis",
                table: "channels",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "remote_balance_satoshis",
                table: "channels",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.Sql(
                "UPDATE channels SET local_balance_satoshis = local_balance_msat / 1000, remote_balance_satoshis = remote_balance_msat / 1000;");

            migrationBuilder.DropColumn(
                name: "local_balance_msat",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "remote_balance_msat",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "short_channel_id",
                table: "channels");
        }
    }
}