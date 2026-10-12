using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddSignerBroadcastSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChannelSignerBroadcastSessions",
                columns: table => new
                {
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    NonceFundingTxId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CommitmentNumber = table.Column<long>(type: "INTEGER", nullable: false),
                    SessionDigest = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelSignerBroadcastSessions", x => new { x.ChannelId, x.NonceFundingTxId, x.CommitmentNumber });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelSignerBroadcastSessions");
        }
    }
}