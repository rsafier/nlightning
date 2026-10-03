using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddCashuProcessorQuotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CashuDeposits",
                columns: table => new
                {
                    TxId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    OutputIndex = table.Column<long>(type: "bigint", nullable: false),
                    QuoteId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AmountSat = table.Column<long>(type: "bigint", nullable: false),
                    BlockHeight = table.Column<long>(type: "bigint", nullable: false),
                    ReportedAt = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashuDeposits", x => new { x.TxId, x.OutputIndex });
                });

            migrationBuilder.CreateTable(
                name: "CashuQuotes",
                columns: table => new
                {
                    QuoteId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Method = table.Column<byte>(type: "tinyint", nullable: false),
                    Direction = table.Column<byte>(type: "tinyint", nullable: false),
                    AmountMsat = table.Column<long>(type: "bigint", nullable: false),
                    MaxFeeMsat = table.Column<long>(type: "bigint", nullable: true),
                    FeeMsat = table.Column<long>(type: "bigint", nullable: true),
                    PaymentHash = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    Address = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Request = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FeeIndex = table.Column<long>(type: "bigint", nullable: true),
                    TxId = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    OutputIndex = table.Column<long>(type: "bigint", nullable: true),
                    State = table.Column<byte>(type: "tinyint", nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashuQuotes", x => x.QuoteId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CashuDeposits_QuoteId",
                table: "CashuDeposits",
                column: "QuoteId");

            migrationBuilder.CreateIndex(
                name: "IX_CashuDeposits_ReportedAt",
                table: "CashuDeposits",
                column: "ReportedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CashuQuotes_Address",
                table: "CashuQuotes",
                column: "Address");

            migrationBuilder.CreateIndex(
                name: "IX_CashuQuotes_Direction_Method_State",
                table: "CashuQuotes",
                columns: new[] { "Direction", "Method", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_CashuQuotes_PaymentHash",
                table: "CashuQuotes",
                column: "PaymentHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CashuDeposits");

            migrationBuilder.DropTable(
                name: "CashuQuotes");
        }
    }
}
