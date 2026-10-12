using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaymentParts",
                columns: table => new
                {
                    PaymentHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    PartIndex = table.Column<byte>(type: "tinyint", nullable: false),
                    ChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    HtlcId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    State = table.Column<byte>(type: "tinyint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentParts", x => new { x.PaymentHash, x.PartIndex });
                    table.ForeignKey(
                        name: "FK_PaymentParts_Payments_PaymentHash",
                        column: x => x.PaymentHash,
                        principalTable: "Payments",
                        principalColumn: "PaymentHash",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PaymentPartHops",
                columns: table => new
                {
                    PaymentHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    PartIndex = table.Column<byte>(type: "tinyint", nullable: false),
                    HopIndex = table.Column<byte>(type: "tinyint", nullable: false),
                    NodeId = table.Column<byte[]>(type: "varbinary(33)", nullable: false),
                    ShortChannelId = table.Column<byte[]>(type: "varbinary(8)", nullable: false),
                    AmountMsat = table.Column<long>(type: "bigint", nullable: false),
                    CltvExpiry = table.Column<long>(type: "bigint", nullable: false),
                    SharedSecret = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    HoldTimeMs = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentPartHops", x => new { x.PaymentHash, x.PartIndex, x.HopIndex });
                    table.ForeignKey(
                        name: "FK_PaymentPartHops_PaymentParts_PaymentHash_PartIndex",
                        columns: x => new { x.PaymentHash, x.PartIndex },
                        principalTable: "PaymentParts",
                        principalColumns: new[] { "PaymentHash", "PartIndex" },
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentPartHops");

            migrationBuilder.DropTable(
                name: "PaymentParts");
        }
    }
}