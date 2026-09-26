using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddGossipGraph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LocalAnnouncementSigsSentAt",
                table: "Channels",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RemoteAnnouncementBitcoinSig",
                table: "Channels",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RemoteAnnouncementNodeSig",
                table: "Channels",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AnnounceChannel",
                table: "ChannelConfigs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "GraphBannedNodes",
                columns: table => new
                {
                    NodeId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Until = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraphBannedNodes", x => x.NodeId);
                });

            migrationBuilder.CreateTable(
                name: "GraphChannels",
                columns: table => new
                {
                    ShortChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    NodeId1 = table.Column<byte[]>(type: "BLOB", nullable: false),
                    NodeId2 = table.Column<byte[]>(type: "BLOB", nullable: false),
                    BitcoinKey1 = table.Column<byte[]>(type: "BLOB", nullable: false),
                    BitcoinKey2 = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CapacitySat = table.Column<long>(type: "INTEGER", nullable: false),
                    Features = table.Column<byte[]>(type: "BLOB", nullable: false),
                    RawAnnouncement = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Verification = table.Column<byte>(type: "INTEGER", nullable: false),
                    SpentAtHeight = table.Column<uint>(type: "INTEGER", nullable: true),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraphChannels", x => x.ShortChannelId);
                });

            migrationBuilder.CreateTable(
                name: "GraphNodes",
                columns: table => new
                {
                    NodeId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Timestamp = table.Column<uint>(type: "INTEGER", nullable: false),
                    Features = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Alias = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Color = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Addresses = table.Column<byte[]>(type: "BLOB", nullable: false),
                    RawAnnouncement = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraphNodes", x => x.NodeId);
                });

            migrationBuilder.CreateTable(
                name: "GraphChannelPolicies",
                columns: table => new
                {
                    ShortChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Direction = table.Column<byte>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<uint>(type: "INTEGER", nullable: false),
                    MessageFlags = table.Column<byte>(type: "INTEGER", nullable: false),
                    ChannelFlags = table.Column<byte>(type: "INTEGER", nullable: false),
                    CltvExpiryDelta = table.Column<ushort>(type: "INTEGER", nullable: false),
                    HtlcMinimumMsat = table.Column<ulong>(type: "INTEGER", nullable: false),
                    HtlcMaximumMsat = table.Column<ulong>(type: "INTEGER", nullable: false),
                    FeeBaseMsat = table.Column<uint>(type: "INTEGER", nullable: false),
                    FeePpm = table.Column<uint>(type: "INTEGER", nullable: false),
                    RawUpdate = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraphChannelPolicies", x => new { x.ShortChannelId, x.Direction });
                    table.ForeignKey(
                        name: "FK_GraphChannelPolicies_GraphChannels_ShortChannelId",
                        column: x => x.ShortChannelId,
                        principalTable: "GraphChannels",
                        principalColumn: "ShortChannelId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GraphChannels_NodeId1",
                table: "GraphChannels",
                column: "NodeId1");

            migrationBuilder.CreateIndex(
                name: "IX_GraphChannels_NodeId2",
                table: "GraphChannels",
                column: "NodeId2");

            migrationBuilder.CreateIndex(
                name: "IX_GraphChannels_SpentAtHeight",
                table: "GraphChannels",
                column: "SpentAtHeight");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GraphBannedNodes");

            migrationBuilder.DropTable(
                name: "GraphChannelPolicies");

            migrationBuilder.DropTable(
                name: "GraphNodes");

            migrationBuilder.DropTable(
                name: "GraphChannels");

            migrationBuilder.DropColumn(
                name: "LocalAnnouncementSigsSentAt",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "RemoteAnnouncementBitcoinSig",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "RemoteAnnouncementNodeSig",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "AnnounceChannel",
                table: "ChannelConfigs");
        }
    }
}