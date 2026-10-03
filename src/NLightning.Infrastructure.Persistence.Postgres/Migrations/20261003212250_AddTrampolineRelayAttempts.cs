using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddTrampolineRelayAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "trampoline_relay_attempts",
                columns: table => new
                {
                    payment_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    next_node_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    amount_out_msat = table.Column<long>(type: "bigint", nullable: false),
                    cltv_expiry_out = table.Column<long>(type: "bigint", nullable: false),
                    incoming_total_msat = table.Column<long>(type: "bigint", nullable: false),
                    incoming_amount_msat = table.Column<long>(type: "bigint", nullable: false),
                    parts = table.Column<int>(type: "integer", nullable: false),
                    incoming_channel_ids = table.Column<byte[]>(type: "bytea", nullable: false),
                    failure_code = table.Column<int>(type: "integer", nullable: true),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                    completed_at = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trampoline_relay_attempts", x => new { x.payment_hash, x.attempt });
                });

            migrationBuilder.CreateIndex(
                name: "ix_trampoline_relay_attempts_created_at",
                table: "trampoline_relay_attempts",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trampoline_relay_attempts");
        }
    }
}