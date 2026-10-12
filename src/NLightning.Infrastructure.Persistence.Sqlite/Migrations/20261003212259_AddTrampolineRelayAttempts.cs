using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddTrampolineRelayAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrampolineRelayAttempts",
                columns: table => new
                {
                    PaymentHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    NextNodeId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    AmountOutMsat = table.Column<long>(type: "INTEGER", nullable: false),
                    CltvExpiryOut = table.Column<uint>(type: "INTEGER", nullable: false),
                    IncomingTotalMsat = table.Column<long>(type: "INTEGER", nullable: false),
                    IncomingAmountMsat = table.Column<long>(type: "INTEGER", nullable: false),
                    Parts = table.Column<int>(type: "INTEGER", nullable: false),
                    IncomingChannelIds = table.Column<byte[]>(type: "BLOB", nullable: false),
                    FailureCode = table.Column<ushort>(type: "INTEGER", nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrampolineRelayAttempts", x => new { x.PaymentHash, x.Attempt });
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrampolineRelayAttempts_CreatedAt",
                table: "TrampolineRelayAttempts",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrampolineRelayAttempts");
        }
    }
}