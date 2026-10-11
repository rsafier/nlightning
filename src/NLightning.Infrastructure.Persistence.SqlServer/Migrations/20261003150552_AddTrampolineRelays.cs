using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddTrampolineRelays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsTrampolineRelay",
                table: "Payments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PaymentTrampolineHops",
                columns: table => new
                {
                    PaymentHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    Attempt = table.Column<int>(type: "int", nullable: false),
                    HopIndex = table.Column<int>(type: "int", nullable: false),
                    NodeId = table.Column<byte[]>(type: "varbinary(33)", nullable: false),
                    SharedSecret = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    AmountMsat = table.Column<long>(type: "bigint", nullable: false),
                    CltvExpiry = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentTrampolineHops", x => new { x.PaymentHash, x.Attempt, x.HopIndex });
                });

            migrationBuilder.CreateTable(
                name: "TrampolineRelays",
                columns: table => new
                {
                    PaymentHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    NextNodeId = table.Column<byte[]>(type: "varbinary(33)", nullable: true),
                    NextEncryptedRecipientData = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    NextPathKey = table.Column<byte[]>(type: "varbinary(33)", nullable: true),
                    RecipientFeatures = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    RecipientBlindedPaths = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    NextTrampolinePacket = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    AmountOutMsat = table.Column<long>(type: "bigint", nullable: false),
                    CltvExpiryOut = table.Column<long>(type: "bigint", nullable: false),
                    IncomingTotalMsat = table.Column<long>(type: "bigint", nullable: false),
                    FeeEarnedMsat = table.Column<long>(type: "bigint", nullable: true),
                    OutgoingPaymentSecret = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    Preimage = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    FailureCode = table.Column<int>(type: "int", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CompletedAt = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrampolineRelays", x => x.PaymentHash);
                });

            migrationBuilder.CreateTable(
                name: "TrampolineRelayParts",
                columns: table => new
                {
                    ChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    HtlcId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    PaymentHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    AmountMsat = table.Column<long>(type: "bigint", nullable: false),
                    CltvExpiry = table.Column<long>(type: "bigint", nullable: false),
                    OuterSharedSecret = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    TrampolineSharedSecret = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    OuterPaymentSecret = table.Column<byte[]>(type: "varbinary(32)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrampolineRelayParts", x => new { x.ChannelId, x.HtlcId });
                    table.ForeignKey(
                        name: "FK_TrampolineRelayParts_TrampolineRelays_PaymentHash",
                        column: x => x.PaymentHash,
                        principalTable: "TrampolineRelays",
                        principalColumn: "PaymentHash",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrampolineRelayParts_PaymentHash",
                table: "TrampolineRelayParts",
                column: "PaymentHash");

            migrationBuilder.CreateIndex(
                name: "IX_TrampolineRelays_CreatedAt",
                table: "TrampolineRelays",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TrampolineRelays_Status",
                table: "TrampolineRelays",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentTrampolineHops");

            migrationBuilder.DropTable(
                name: "TrampolineRelayParts");

            migrationBuilder.DropTable(
                name: "TrampolineRelays");

            migrationBuilder.DropColumn(
                name: "IsTrampolineRelay",
                table: "Payments");
        }
    }
}