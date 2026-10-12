using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
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
                    NodeId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Blob = table.Column<byte[]>(type: "BLOB", nullable: false),
                    MatchesLastSent = table.Column<bool>(type: "INTEGER", nullable: true),
                    UnknownChannelIds = table.Column<byte[]>(type: "BLOB", nullable: false)
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