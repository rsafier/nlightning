using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountingEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PushAmountMsat",
                table: "Channels",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FeeSat",
                table: "BroadcastTransactions",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccountingEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<long>(type: "bigint", nullable: false),
                    BlockHeight = table.Column<long>(type: "bigint", nullable: true),
                    ChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    ShortChannelId = table.Column<byte[]>(type: "varbinary(8)", nullable: true),
                    PaymentHash = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    TxId = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    OutputIndex = table.Column<long>(type: "bigint", nullable: true),
                    Counterparty = table.Column<byte[]>(type: "varbinary(33)", nullable: true),
                    AmountMsat = table.Column<long>(type: "bigint", nullable: false),
                    FeeMsat = table.Column<long>(type: "bigint", nullable: false),
                    Finality = table.Column<byte>(type: "tinyint", nullable: false),
                    Flags = table.Column<int>(type: "int", nullable: false),
                    Details = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LedgerSeq = table.Column<long>(type: "bigint", nullable: true),
                    Hash = table.Column<byte[]>(type: "varbinary(32)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEvents_ChannelId",
                table: "AccountingEvents",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEvents_EventKey",
                table: "AccountingEvents",
                column: "EventKey");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEvents_LedgerSeq",
                table: "AccountingEvents",
                column: "LedgerSeq");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEvents_OccurredAt",
                table: "AccountingEvents",
                column: "OccurredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountingEvents");

            migrationBuilder.DropColumn(
                name: "PushAmountMsat",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "FeeSat",
                table: "BroadcastTransactions");
        }
    }
}