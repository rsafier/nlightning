using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
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
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PaymentTrampolineHops",
                columns: table => new
                {
                    PaymentHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    HopIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    NodeId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SharedSecret = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AmountMsat = table.Column<long>(type: "INTEGER", nullable: false),
                    CltvExpiry = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentTrampolineHops", x => new { x.PaymentHash, x.Attempt, x.HopIndex });
                });

            migrationBuilder.CreateTable(
                name: "TrampolineRelays",
                columns: table => new
                {
                    PaymentHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Status = table.Column<byte>(type: "INTEGER", nullable: false),
                    NextNodeId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    NextEncryptedRecipientData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    NextPathKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    RecipientFeatures = table.Column<byte[]>(type: "BLOB", nullable: true),
                    RecipientBlindedPaths = table.Column<byte[]>(type: "BLOB", nullable: true),
                    NextTrampolinePacket = table.Column<byte[]>(type: "BLOB", nullable: true),
                    AmountOutMsat = table.Column<long>(type: "INTEGER", nullable: false),
                    CltvExpiryOut = table.Column<uint>(type: "INTEGER", nullable: false),
                    IncomingTotalMsat = table.Column<long>(type: "INTEGER", nullable: false),
                    FeeEarnedMsat = table.Column<long>(type: "INTEGER", nullable: true),
                    OutgoingPaymentSecret = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Preimage = table.Column<byte[]>(type: "BLOB", nullable: true),
                    FailureCode = table.Column<ushort>(type: "INTEGER", nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrampolineRelays", x => x.PaymentHash);
                });

            migrationBuilder.CreateTable(
                name: "TrampolineRelayParts",
                columns: table => new
                {
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    HtlcId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    PaymentHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AmountMsat = table.Column<long>(type: "INTEGER", nullable: false),
                    CltvExpiry = table.Column<uint>(type: "INTEGER", nullable: false),
                    OuterSharedSecret = table.Column<byte[]>(type: "BLOB", nullable: false),
                    TrampolineSharedSecret = table.Column<byte[]>(type: "BLOB", nullable: false),
                    OuterPaymentSecret = table.Column<byte[]>(type: "BLOB", nullable: true)
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