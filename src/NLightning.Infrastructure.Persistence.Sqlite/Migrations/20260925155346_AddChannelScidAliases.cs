using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelScidAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "RemoteAlias",
                table: "Channels",
                type: "BLOB",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChannelLocalAliases",
                columns: table => new
                {
                    Alias = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelLocalAliases", x => x.Alias);
                    table.ForeignKey(
                        name: "FK_ChannelLocalAliases_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "ChannelId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelLocalAliases_ChannelId",
                table: "ChannelLocalAliases",
                column: "ChannelId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelLocalAliases");

            migrationBuilder.DropColumn(
                name: "RemoteAlias",
                table: "Channels");
        }
    }
}