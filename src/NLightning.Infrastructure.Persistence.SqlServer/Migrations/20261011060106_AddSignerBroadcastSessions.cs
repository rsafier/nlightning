using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
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
                    ChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    NonceFundingTxId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    CommitmentNumber = table.Column<long>(type: "bigint", nullable: false),
                    SessionDigest = table.Column<byte[]>(type: "varbinary(32)", nullable: false)
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