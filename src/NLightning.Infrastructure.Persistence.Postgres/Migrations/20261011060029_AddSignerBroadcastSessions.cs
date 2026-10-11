using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSignerBroadcastSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "channel_signer_broadcast_sessions",
                columns: table => new
                {
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    nonce_funding_tx_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    commitment_number = table.Column<long>(type: "bigint", nullable: false),
                    session_digest = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_signer_broadcast_sessions", x => new { x.channel_id, x.nonce_funding_tx_id, x.commitment_number });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_signer_broadcast_sessions");
        }
    }
}