using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddInteractiveTxSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "interactive_tx_sessions",
                columns: table => new
                {
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<byte>(type: "smallint", nullable: false),
                    is_initiator = table.Column<bool>(type: "boolean", nullable: false),
                    feerate_per_kw = table.Column<long>(type: "bigint", nullable: false),
                    locktime = table.Column<long>(type: "bigint", nullable: false),
                    inputs = table.Column<byte[]>(type: "bytea", nullable: false),
                    outputs = table.Column<byte[]>(type: "bytea", nullable: false),
                    local_contribution = table.Column<byte[]>(type: "bytea", nullable: false),
                    local_reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    constructed_tx = table.Column<byte[]>(type: "bytea", nullable: true),
                    our_witnesses = table.Column<byte[]>(type: "bytea", nullable: true),
                    their_witnesses = table.Column<byte[]>(type: "bytea", nullable: true),
                    our_shared_input_signature = table.Column<byte[]>(type: "bytea", nullable: true),
                    their_shared_input_signature = table.Column<byte[]>(type: "bytea", nullable: true),
                    commitment_signed_sent = table.Column<bool>(type: "boolean", nullable: false),
                    commitment_signed_received = table.Column<bool>(type: "boolean", nullable: false),
                    tx_signatures_sent = table.Column<bool>(type: "boolean", nullable: false),
                    tx_signatures_received = table.Column<bool>(type: "boolean", nullable: false),
                    state = table.Column<byte>(type: "smallint", nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                    resolved_at = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_interactive_tx_sessions", x => new { x.channel_id, x.session_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_interactive_tx_sessions_resolved_at",
                table: "interactive_tx_sessions",
                column: "resolved_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "interactive_tx_sessions");
        }
    }
}