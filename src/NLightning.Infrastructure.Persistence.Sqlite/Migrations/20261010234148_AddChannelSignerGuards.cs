using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
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
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    LocalCommitmentNumber = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedCommitmentNumber = table.Column<long>(type: "INTEGER", nullable: true),
                    RemoteSignedCommitmentNumber = table.Column<long>(type: "INTEGER", nullable: true),
                    BroadcastSignedCommitmentNumber = table.Column<long>(type: "INTEGER", nullable: true),
                    DataLossDetected = table.Column<bool>(type: "INTEGER", nullable: false)
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