using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelSignerGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChannelSignerGuards",
                columns: table => new
                {
                    ChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    LocalCommitmentNumber = table.Column<long>(type: "bigint", nullable: false),
                    RevokedCommitmentNumber = table.Column<long>(type: "bigint", nullable: true),
                    RemoteSignedCommitmentNumber = table.Column<long>(type: "bigint", nullable: true),
                    BroadcastSignedCommitmentNumber = table.Column<long>(type: "bigint", nullable: true),
                    DataLossDetected = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelSignerGuards", x => x.ChannelId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelSignerGuards");
        }
    }
}