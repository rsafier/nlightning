using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelSignerGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "channel_signer_guards",
                columns: table => new
                {
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    local_commitment_number = table.Column<long>(type: "bigint", nullable: false),
                    revoked_commitment_number = table.Column<long>(type: "bigint", nullable: true),
                    remote_signed_commitment_number = table.Column<long>(type: "bigint", nullable: true),
                    broadcast_signed_commitment_number = table.Column<long>(type: "bigint", nullable: true),
                    data_loss_detected = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_signer_guards", x => x.channel_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_signer_guards");
        }
    }
}