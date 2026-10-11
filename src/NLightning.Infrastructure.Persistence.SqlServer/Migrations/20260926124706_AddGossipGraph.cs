using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
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
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RemoteAnnouncementBitcoinSig",
                table: "Channels",
                type: "varbinary(64)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RemoteAnnouncementNodeSig",
                table: "Channels",
                type: "varbinary(64)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AnnounceChannel",
                table: "ChannelConfigs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "GraphBannedNodes",
                columns: table => new
                {
                    NodeId = table.Column<byte[]>(type: "varbinary(33)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Until = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraphBannedNodes", x => x.NodeId);
                });

            migrationBuilder.CreateTable(
                name: "GraphChannels",
                columns: table => new
                {
                    ShortChannelId = table.Column<byte[]>(type: "varbinary(8)", nullable: false),
                    NodeId1 = table.Column<byte[]>(type: "varbinary(33)", nullable: false),
                    NodeId2 = table.Column<byte[]>(type: "varbinary(33)", nullable: false),
                    BitcoinKey1 = table.Column<byte[]>(type: "varbinary(33)", nullable: false),
                    BitcoinKey2 = table.Column<byte[]>(type: "varbinary(33)", nullable: false),
                    CapacitySat = table.Column<long>(type: "bigint", nullable: false),
                    Features = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    RawAnnouncement = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Verification = table.Column<byte>(type: "tinyint", nullable: false),
                    SpentAtHeight = table.Column<long>(type: "bigint", nullable: true),
                    ReceivedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraphChannels", x => x.ShortChannelId);
                });

            migrationBuilder.CreateTable(
                name: "GraphNodes",
                columns: table => new
                {
                    NodeId = table.Column<byte[]>(type: "varbinary(33)", nullable: false),
                    Timestamp = table.Column<long>(type: "bigint", nullable: false),
                    Features = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Alias = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    Color = table.Column<byte[]>(type: "varbinary(3)", nullable: false),
                    Addresses = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    RawAnnouncement = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    ReceivedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraphNodes", x => x.NodeId);
                });

            migrationBuilder.CreateTable(
                name: "GraphChannelPolicies",
                columns: table => new
                {
                    ShortChannelId = table.Column<byte[]>(type: "varbinary(8)", nullable: false),
                    Direction = table.Column<byte>(type: "tinyint", nullable: false),
                    Timestamp = table.Column<long>(type: "bigint", nullable: false),
                    MessageFlags = table.Column<byte>(type: "tinyint", nullable: false),
                    ChannelFlags = table.Column<byte>(type: "tinyint", nullable: false),
                    CltvExpiryDelta = table.Column<int>(type: "int", nullable: false),
                    HtlcMinimumMsat = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    HtlcMaximumMsat = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    FeeBaseMsat = table.Column<long>(type: "bigint", nullable: false),
                    FeePpm = table.Column<long>(type: "bigint", nullable: false),
                    RawUpdate = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
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