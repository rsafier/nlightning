using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
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

            migrationBuilder.AddColumn<long>(
                name: "BlockHeight",
                table: "GraphNodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "GossipVersions",
                table: "GraphNodes",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)1);

            migrationBuilder.AddColumn<byte[]>(
                name: "RawAnnouncement2",
                table: "GraphNodes",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AlterColumn<byte[]>(
                name: "BitcoinKey2",
                table: "GraphChannels",
                type: "varbinary(33)",
                nullable: true,
                oldClrType: typeof(byte[]),
                oldType: "varbinary(33)");

            migrationBuilder.AlterColumn<byte[]>(
                name: "BitcoinKey1",
                table: "GraphChannels",
                type: "varbinary(33)",
                nullable: true,
                oldClrType: typeof(byte[]),
                oldType: "varbinary(33)");

            migrationBuilder.AddColumn<byte>(
                name: "GossipVersions",
                table: "GraphChannels",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)1);

            migrationBuilder.AddColumn<byte[]>(
                name: "RawAnnouncement2",
                table: "GraphChannels",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "Version",
                table: "GraphChannelPolicies",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)1);

            migrationBuilder.AddColumn<long>(
                name: "InboundFeeBaseMsat",
                table: "GraphChannelPolicies",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "InboundFeePpm",
                table: "GraphChannelPolicies",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

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
                type: "varbinary(33)",
                nullable: false,
                defaultValue: new byte[0],
                oldClrType: typeof(byte[]),
                oldType: "varbinary(33)",
                oldNullable: true);

            migrationBuilder.AlterColumn<byte[]>(
                name: "BitcoinKey1",
                table: "GraphChannels",
                type: "varbinary(33)",
                nullable: false,
                defaultValue: new byte[0],
                oldClrType: typeof(byte[]),
                oldType: "varbinary(33)",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_GraphChannelPolicies",
                table: "GraphChannelPolicies",
                columns: new[] { "ShortChannelId", "Direction" });
        }
    }
}