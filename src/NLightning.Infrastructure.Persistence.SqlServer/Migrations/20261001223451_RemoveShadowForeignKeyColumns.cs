using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class RemoveShadowForeignKeyColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Channels_Peers_PeerEntityNodeId",
                table: "Channels");

            migrationBuilder.DropIndex(
                name: "IX_Channels_PeerEntityNodeId",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "ChangeAddressType",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "PeerEntityNodeId",
                table: "Channels");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "ChangeAddressType",
                table: "Channels",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "PeerEntityNodeId",
                table: "Channels",
                type: "varbinary(33)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Channels_PeerEntityNodeId",
                table: "Channels",
                column: "PeerEntityNodeId");

            migrationBuilder.AddForeignKey(
                name: "FK_Channels_Peers_PeerEntityNodeId",
                table: "Channels",
                column: "PeerEntityNodeId",
                principalTable: "Peers",
                principalColumn: "NodeId");
        }
    }
}