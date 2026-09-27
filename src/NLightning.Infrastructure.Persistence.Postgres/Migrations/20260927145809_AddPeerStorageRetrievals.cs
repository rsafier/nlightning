using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddPeerStorageRetrievals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "peer_storage_retrievals",
                columns: table => new
                {
                    node_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    received_at = table.Column<long>(type: "bigint", nullable: false),
                    blob = table.Column<byte[]>(type: "bytea", nullable: false),
                    matches_last_sent = table.Column<bool>(type: "boolean", nullable: true),
                    unknown_channel_ids = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_peer_storage_retrievals", x => x.node_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "peer_storage_retrievals");
        }
    }
}