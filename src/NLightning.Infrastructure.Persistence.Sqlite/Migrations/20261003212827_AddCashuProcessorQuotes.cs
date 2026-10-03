using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
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
                    TxId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    OutputIndex = table.Column<uint>(type: "INTEGER", nullable: false),
                    QuoteId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AmountSat = table.Column<long>(type: "INTEGER", nullable: false),
                    BlockHeight = table.Column<uint>(type: "INTEGER", nullable: false),
                    ReportedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashuDeposits", x => new { x.TxId, x.OutputIndex });
                });

            migrationBuilder.CreateTable(
                name: "CashuQuotes",
                columns: table => new
                {
                    QuoteId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Method = table.Column<byte>(type: "INTEGER", nullable: false),
                    Direction = table.Column<byte>(type: "INTEGER", nullable: false),
                    AmountMsat = table.Column<long>(type: "INTEGER", nullable: false),
                    MaxFeeMsat = table.Column<long>(type: "INTEGER", nullable: true),
                    FeeMsat = table.Column<long>(type: "INTEGER", nullable: true),
                    PaymentHash = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Address = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Request = table.Column<string>(type: "TEXT", nullable: true),
                    FeeIndex = table.Column<uint>(type: "INTEGER", nullable: true),
                    TxId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    OutputIndex = table.Column<uint>(type: "INTEGER", nullable: true),
                    State = table.Column<byte>(type: "INTEGER", nullable: false),
                    FailureReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
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