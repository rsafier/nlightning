using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
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
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FeeSat",
                table: "BroadcastTransactions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccountingEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    BlockHeight = table.Column<uint>(type: "INTEGER", nullable: true),
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ShortChannelId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    PaymentHash = table.Column<byte[]>(type: "BLOB", nullable: true),
                    TxId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    OutputIndex = table.Column<uint>(type: "INTEGER", nullable: true),
                    Counterparty = table.Column<byte[]>(type: "BLOB", nullable: true),
                    AmountMsat = table.Column<long>(type: "INTEGER", nullable: false),
                    FeeMsat = table.Column<long>(type: "INTEGER", nullable: false),
                    Finality = table.Column<byte>(type: "INTEGER", nullable: false),
                    Flags = table.Column<int>(type: "INTEGER", nullable: false),
                    Details = table.Column<string>(type: "TEXT", nullable: true),
                    LedgerSeq = table.Column<long>(type: "INTEGER", nullable: true),
                    Hash = table.Column<byte[]>(type: "BLOB", nullable: true)
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