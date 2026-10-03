using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoicesPaymentsAndCircuits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "OriginIncomingChannelId",
                table: "Htlcs",
                type: "varbinary(32)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OriginIncomingHtlcId",
                table: "Htlcs",
                type: "decimal(20,0)",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "OriginKind",
                table: "Htlcs",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "OriginPaymentHash",
                table: "Htlcs",
                type: "varbinary(32)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxDustHtlcExposureMsat",
                table: "Channels",
                type: "decimal(20,0)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ForwardCircuits",
                columns: table => new
                {
                    IncomingChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    IncomingHtlcId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    IncomingAmountMsat = table.Column<long>(type: "bigint", nullable: false),
                    IncomingCltvExpiry = table.Column<long>(type: "bigint", nullable: false),
                    PaymentHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    IncomingSharedSecret = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    OutgoingShortChannelId = table.Column<byte[]>(type: "varbinary(8)", nullable: false),
                    OutgoingAmountMsat = table.Column<long>(type: "bigint", nullable: false),
                    OutgoingCltvExpiry = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    OutgoingChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    OutgoingHtlcId = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    ResolvedAt = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForwardCircuits", x => new { x.IncomingChannelId, x.IncomingHtlcId });
                });

            migrationBuilder.CreateTable(
                name: "Invoices",
                columns: table => new
                {
                    PaymentHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    Preimage = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    PaymentSecret = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    AmountMsat = table.Column<long>(type: "bigint", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Bolt11 = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    ExpirySeconds = table.Column<long>(type: "bigint", nullable: false),
                    MinFinalCltvExpiry = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    AmountReceivedMsat = table.Column<long>(type: "bigint", nullable: true),
                    SettledAt = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoices", x => x.PaymentHash);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    PaymentHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    Bolt11 = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PayeeNodeId = table.Column<byte[]>(type: "varbinary(33)", nullable: false),
                    AmountMsat = table.Column<long>(type: "bigint", nullable: false),
                    FeeMsat = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    OutgoingChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    OutgoingHtlcId = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    Preimage = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    FailureCode = table.Column<int>(type: "int", nullable: true),
                    FailureSourceIndex = table.Column<int>(type: "int", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CompletedAt = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.PaymentHash);
                });

            migrationBuilder.CreateTable(
                name: "PaymentHops",
                columns: table => new
                {
                    PaymentHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    HopIndex = table.Column<byte>(type: "tinyint", nullable: false),
                    NodeId = table.Column<byte[]>(type: "varbinary(33)", nullable: false),
                    ShortChannelId = table.Column<byte[]>(type: "varbinary(8)", nullable: false),
                    AmountMsat = table.Column<long>(type: "bigint", nullable: false),
                    CltvExpiry = table.Column<long>(type: "bigint", nullable: false),
                    SharedSecret = table.Column<byte[]>(type: "varbinary(32)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentHops", x => new { x.PaymentHash, x.HopIndex });
                    table.ForeignKey(
                        name: "FK_PaymentHops_Payments_PaymentHash",
                        column: x => x.PaymentHash,
                        principalTable: "Payments",
                        principalColumn: "PaymentHash",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Htlcs_OriginIncomingChannelId_OriginIncomingHtlcId",
                table: "Htlcs",
                columns: new[] { "OriginIncomingChannelId", "OriginIncomingHtlcId" });

            migrationBuilder.CreateIndex(
                name: "IX_Htlcs_OriginPaymentHash",
                table: "Htlcs",
                column: "OriginPaymentHash");

            migrationBuilder.CreateIndex(
                name: "IX_ForwardCircuits_OutgoingChannelId_OutgoingHtlcId",
                table: "ForwardCircuits",
                columns: new[] { "OutgoingChannelId", "OutgoingHtlcId" });

            migrationBuilder.CreateIndex(
                name: "IX_ForwardCircuits_Status",
                table: "ForwardCircuits",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_CreatedAt",
                table: "Invoices",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CreatedAt",
                table: "Payments",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Status",
                table: "Payments",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ForwardCircuits");

            migrationBuilder.DropTable(
                name: "Invoices");

            migrationBuilder.DropTable(
                name: "PaymentHops");

            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Htlcs_OriginIncomingChannelId_OriginIncomingHtlcId",
                table: "Htlcs");

            migrationBuilder.DropIndex(
                name: "IX_Htlcs_OriginPaymentHash",
                table: "Htlcs");

            migrationBuilder.DropColumn(
                name: "OriginIncomingChannelId",
                table: "Htlcs");

            migrationBuilder.DropColumn(
                name: "OriginIncomingHtlcId",
                table: "Htlcs");

            migrationBuilder.DropColumn(
                name: "OriginKind",
                table: "Htlcs");

            migrationBuilder.DropColumn(
                name: "OriginPaymentHash",
                table: "Htlcs");

            migrationBuilder.DropColumn(
                name: "MaxDustHtlcExposureMsat",
                table: "Channels");
        }
    }
}