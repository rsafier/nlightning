using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class RemoveShadowForeignKeyColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_channels_peers_peer_entity_node_id",
                table: "channels");

            migrationBuilder.DropIndex(
                name: "ix_channels_peer_entity_node_id",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "change_address_type",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "peer_entity_node_id",
                table: "channels");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "change_address_type",
                table: "channels",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "peer_entity_node_id",
                table: "channels",
                type: "bytea",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_channels_peer_entity_node_id",
                table: "channels",
                column: "peer_entity_node_id");

            migrationBuilder.AddForeignKey(
                name: "fk_channels_peers_peer_entity_node_id",
                table: "channels",
                column: "peer_entity_node_id",
                principalTable: "peers",
                principalColumn: "node_id");
        }
    }
}