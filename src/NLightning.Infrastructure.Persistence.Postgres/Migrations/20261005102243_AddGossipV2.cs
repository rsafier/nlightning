using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddGossipV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NL-878 (taproot gossip): the existing rows are BOLT 7 gossip, so the new version columns default to 1
            migrationBuilder.DropPrimaryKey(
                name: "pk_graph_channel_policies",
                table: "graph_channel_policies");

            migrationBuilder.AddColumn<long>(
                name: "block_height",
                table: "graph_nodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "gossip_versions",
                table: "graph_nodes",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)1);

            migrationBuilder.AddColumn<byte[]>(
                name: "raw_announcement2",
                table: "graph_nodes",
                type: "bytea",
                nullable: true);

            migrationBuilder.AlterColumn<byte[]>(
                name: "bitcoin_key2",
                table: "graph_channels",
                type: "bytea",
                nullable: true,
                oldClrType: typeof(byte[]),
                oldType: "bytea");

            migrationBuilder.AlterColumn<byte[]>(
                name: "bitcoin_key1",
                table: "graph_channels",
                type: "bytea",
                nullable: true,
                oldClrType: typeof(byte[]),
                oldType: "bytea");

            migrationBuilder.AddColumn<byte>(
                name: "gossip_versions",
                table: "graph_channels",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)1);

            migrationBuilder.AddColumn<byte[]>(
                name: "raw_announcement2",
                table: "graph_channels",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "version",
                table: "graph_channel_policies",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)1);

            migrationBuilder.AddColumn<long>(
                name: "inbound_fee_base_msat",
                table: "graph_channel_policies",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "inbound_fee_ppm",
                table: "graph_channel_policies",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddPrimaryKey(
                name: "pk_graph_channel_policies",
                table: "graph_channel_policies",
                columns: new[] { "short_channel_id", "direction", "version" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_graph_channel_policies",
                table: "graph_channel_policies");

            migrationBuilder.DropColumn(
                name: "block_height",
                table: "graph_nodes");

            migrationBuilder.DropColumn(
                name: "gossip_versions",
                table: "graph_nodes");

            migrationBuilder.DropColumn(
                name: "raw_announcement2",
                table: "graph_nodes");

            migrationBuilder.DropColumn(
                name: "gossip_versions",
                table: "graph_channels");

            migrationBuilder.DropColumn(
                name: "raw_announcement2",
                table: "graph_channels");

            migrationBuilder.DropColumn(
                name: "version",
                table: "graph_channel_policies");

            migrationBuilder.DropColumn(
                name: "inbound_fee_base_msat",
                table: "graph_channel_policies");

            migrationBuilder.DropColumn(
                name: "inbound_fee_ppm",
                table: "graph_channel_policies");

            migrationBuilder.AlterColumn<byte[]>(
                name: "bitcoin_key2",
                table: "graph_channels",
                type: "bytea",
                nullable: false,
                defaultValue: new byte[0],
                oldClrType: typeof(byte[]),
                oldType: "bytea",
                oldNullable: true);

            migrationBuilder.AlterColumn<byte[]>(
                name: "bitcoin_key1",
                table: "graph_channels",
                type: "bytea",
                nullable: false,
                defaultValue: new byte[0],
                oldClrType: typeof(byte[]),
                oldType: "bytea",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "pk_graph_channel_policies",
                table: "graph_channel_policies",
                columns: new[] { "short_channel_id", "direction" });
        }
    }
}