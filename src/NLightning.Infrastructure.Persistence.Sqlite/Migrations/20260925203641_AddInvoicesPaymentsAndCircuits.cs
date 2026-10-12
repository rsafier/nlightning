using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
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
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<ulong>(
                name: "OriginIncomingHtlcId",
                table: "Htlcs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "OriginKind",
                table: "Htlcs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "OriginPaymentHash",
                table: "Htlcs",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<ulong>(
                name: "MaxDustHtlcExposureMsat",
                table: "Channels",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ForwardCircuits",
                columns: table => new
                {
                    IncomingChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    IncomingHtlcId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    IncomingAmountMsat = table.Column<long>(type: "INTEGER", nullable: false),
                    IncomingCltvExpiry = table.Column<uint>(type: "INTEGER", nullable: false),
                    PaymentHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    IncomingSharedSecret = table.Column<byte[]>(type: "BLOB", nullable: false),
                    OutgoingShortChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    OutgoingAmountMsat = table.Column<long>(type: "INTEGER", nullable: false),
                    OutgoingCltvExpiry = table.Column<uint>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<byte>(type: "INTEGER", nullable: false),
                    OutgoingChannelId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    OutgoingHtlcId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    ResolvedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForwardCircuits", x => new { x.IncomingChannelId, x.IncomingHtlcId });
                });

            migrationBuilder.CreateTable(
                name: "Invoices",
                columns: table => new
                {
                    PaymentHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Preimage = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PaymentSecret = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AmountMsat = table.Column<long>(type: "INTEGER", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Bolt11 = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpirySeconds = table.Column<uint>(type: "INTEGER", nullable: false),
                    MinFinalCltvExpiry = table.Column<ushort>(type: "INTEGER", nullable: false),
                    Status = table.Column<byte>(type: "INTEGER", nullable: false),
                    AmountReceivedMsat = table.Column<long>(type: "INTEGER", nullable: true),
                    SettledAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoices", x => x.PaymentHash);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    PaymentHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Bolt11 = table.Column<string>(type: "TEXT", nullable: true),
                    PayeeNodeId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AmountMsat = table.Column<long>(type: "INTEGER", nullable: false),
                    FeeMsat = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<byte>(type: "INTEGER", nullable: false),
                    OutgoingChannelId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    OutgoingHtlcId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    Preimage = table.Column<byte[]>(type: "BLOB", nullable: true),
                    FailureCode = table.Column<ushort>(type: "INTEGER", nullable: true),
                    FailureSourceIndex = table.Column<int>(type: "INTEGER", nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.PaymentHash);
                });

            migrationBuilder.CreateTable(
                name: "PaymentHops",
                columns: table => new
                {
                    PaymentHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    HopIndex = table.Column<byte>(type: "INTEGER", nullable: false),
                    NodeId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ShortChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AmountMsat = table.Column<long>(type: "INTEGER", nullable: false),
                    CltvExpiry = table.Column<uint>(type: "INTEGER", nullable: false),
                    SharedSecret = table.Column<byte[]>(type: "BLOB", nullable: false)
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