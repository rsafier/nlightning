using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
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
                    PaymentHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    Attempt = table.Column<int>(type: "int", nullable: false),
                    NextNodeId = table.Column<byte[]>(type: "varbinary(33)", nullable: true),
                    AmountOutMsat = table.Column<long>(type: "bigint", nullable: false),
                    CltvExpiryOut = table.Column<long>(type: "bigint", nullable: false),
                    IncomingTotalMsat = table.Column<long>(type: "bigint", nullable: false),
                    IncomingAmountMsat = table.Column<long>(type: "bigint", nullable: false),
                    Parts = table.Column<int>(type: "int", nullable: false),
                    IncomingChannelIds = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    FailureCode = table.Column<int>(type: "int", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CompletedAt = table.Column<long>(type: "bigint", nullable: true)
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