using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddPeerStorageRetrievals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PeerStorageRetrievals",
                columns: table => new
                {
                    NodeId = table.Column<byte[]>(type: "varbinary(33)", nullable: false),
                    ReceivedAt = table.Column<long>(type: "bigint", nullable: false),
                    Blob = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    MatchesLastSent = table.Column<bool>(type: "bit", nullable: true),
                    UnknownChannelIds = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerStorageRetrievals", x => x.NodeId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PeerStorageRetrievals");
        }
    }
}