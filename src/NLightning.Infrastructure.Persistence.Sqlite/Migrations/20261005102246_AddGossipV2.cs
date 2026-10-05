using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddGossipV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NL-878 (taproot gossip): the existing rows are BOLT 7 gossip, so the new version columns default to 1
            migrationBuilder.DropPrimaryKey(
                name: "PK_GraphChannelPolicies",
                table: "GraphChannelPolicies");

            migrationBuilder.AddColumn<uint>(
                name: "BlockHeight",
                table: "GraphNodes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "GossipVersions",
                table: "GraphNodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: (byte)1);

            migrationBuilder.AddColumn<byte[]>(
                name: "RawAnnouncement2",
                table: "GraphNodes",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AlterColumn<byte[]>(
                name: "BitcoinKey2",
                table: "GraphChannels",
                type: "BLOB",
                nullable: true,
                oldClrType: typeof(byte[]),
                oldType: "BLOB");

            migrationBuilder.AlterColumn<byte[]>(
                name: "BitcoinKey1",
                table: "GraphChannels",
                type: "BLOB",
                nullable: true,
                oldClrType: typeof(byte[]),
                oldType: "BLOB");

            migrationBuilder.AddColumn<byte>(
                name: "GossipVersions",
                table: "GraphChannels",
                type: "INTEGER",
                nullable: false,
                defaultValue: (byte)1);

            migrationBuilder.AddColumn<byte[]>(
                name: "RawAnnouncement2",
                table: "GraphChannels",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "Version",
                table: "GraphChannelPolicies",
                type: "INTEGER",
                nullable: false,
                defaultValue: (byte)1);

            migrationBuilder.AddColumn<uint>(
                name: "InboundFeeBaseMsat",
                table: "GraphChannelPolicies",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "InboundFeePpm",
                table: "GraphChannelPolicies",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddPrimaryKey(
                name: "PK_GraphChannelPolicies",
                table: "GraphChannelPolicies",
                columns: new[] { "ShortChannelId", "Direction", "Version" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_GraphChannelPolicies",
                table: "GraphChannelPolicies");

            migrationBuilder.DropColumn(
                name: "BlockHeight",
                table: "GraphNodes");

            migrationBuilder.DropColumn(
                name: "GossipVersions",
                table: "GraphNodes");

            migrationBuilder.DropColumn(
                name: "RawAnnouncement2",
                table: "GraphNodes");

            migrationBuilder.DropColumn(
                name: "GossipVersions",
                table: "GraphChannels");

            migrationBuilder.DropColumn(
                name: "RawAnnouncement2",
                table: "GraphChannels");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "GraphChannelPolicies");

            migrationBuilder.DropColumn(
                name: "InboundFeeBaseMsat",
                table: "GraphChannelPolicies");

            migrationBuilder.DropColumn(
                name: "InboundFeePpm",
                table: "GraphChannelPolicies");

            migrationBuilder.AlterColumn<byte[]>(
                name: "BitcoinKey2",
                table: "GraphChannels",
                type: "BLOB",
                nullable: false,
                defaultValue: new byte[0],
                oldClrType: typeof(byte[]),
                oldType: "BLOB",
                oldNullable: true);

            migrationBuilder.AlterColumn<byte[]>(
                name: "BitcoinKey1",
                table: "GraphChannels",
                type: "BLOB",
                nullable: false,
                defaultValue: new byte[0],
                oldClrType: typeof(byte[]),
                oldType: "BLOB",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_GraphChannelPolicies",
                table: "GraphChannelPolicies",
                columns: new[] { "ShortChannelId", "Direction" });
        }
    }
}