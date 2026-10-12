using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddOnionReplaySet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "onion_replay_entries",
                columns: table => new
                {
                    hmac = table.Column<byte[]>(type: "bytea", nullable: false),
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    htlc_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    expiry_height = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_onion_replay_entries", x => x.hmac);
                });

            migrationBuilder.CreateIndex(
                name: "ix_onion_replay_entries_expiry_height",
                table: "onion_replay_entries",
                column: "expiry_height");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "onion_replay_entries");
        }
    }
}