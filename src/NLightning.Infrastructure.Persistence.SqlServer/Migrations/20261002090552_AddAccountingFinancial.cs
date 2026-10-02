using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountingFinancial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccountingPostings_AccountingEntries_LedgerSeq",
                table: "AccountingPostings");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AccountingPostings",
                table: "AccountingPostings");

            migrationBuilder.DropIndex(
                name: "IX_AccountingPostings_Account_OccurredAt",
                table: "AccountingPostings");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AccountingEntries",
                table: "AccountingEntries");

            migrationBuilder.DropIndex(
                name: "IX_AccountingEntries_EventKey",
                table: "AccountingEntries");

            migrationBuilder.DropIndex(
                name: "IX_AccountingEntries_OccurredAt",
                table: "AccountingEntries");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AccountingCursor",
                table: "AccountingCursor");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AccountingBalances",
                table: "AccountingBalances");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "AccountingCursor");

            migrationBuilder.AddColumn<string>(
                name: "Label",
                table: "Payments",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "Payments",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Label",
                table: "Offers",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "Offers",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Label",
                table: "Invoices",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "Invoices",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Label",
                table: "Channels",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "Channels",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Label",
                table: "BroadcastTransactions",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "BroadcastTransactions",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "Book",
                table: "AccountingPostings",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<int>(
                name: "Adjustment",
                table: "AccountingPostings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AccountName",
                table: "AccountingPostings",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FiatAmount",
                table: "AccountingPostings",
                type: "decimal(28,8)",
                precision: 28,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FiatCurrency",
                table: "AccountingPostings",
                type: "nchar(3)",
                fixedLength: true,
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PriceId",
                table: "AccountingPostings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "Book",
                table: "AccountingEntries",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<int>(
                name: "Adjustment",
                table: "AccountingEntries",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<byte>(
                name: "Classification",
                table: "AccountingEntries",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClosedPeriodId",
                table: "AccountingEntries",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Flags",
                table: "AccountingEntries",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "RuleId",
                table: "AccountingEntries",
                type: "bigint",
                nullable: true);

            // The A2 singleton row (Id 1) becomes the operational book's cursor (Book 0) through the column default
            migrationBuilder.AddColumn<byte>(
                name: "Book",
                table: "AccountingCursor",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<byte>(
                name: "Book",
                table: "AccountingBalances",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<string>(
                name: "AccountName",
                table: "AccountingBalances",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "FiatAmount",
                table: "AccountingBalances",
                type: "decimal(28,8)",
                precision: 28,
                scale: 8,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddPrimaryKey(
                name: "PK_AccountingPostings",
                table: "AccountingPostings",
                columns: new[] { "Book", "LedgerSeq", "Adjustment", "Index" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_AccountingEntries",
                table: "AccountingEntries",
                columns: new[] { "Book", "LedgerSeq", "Adjustment" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_AccountingCursor",
                table: "AccountingCursor",
                column: "Book");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AccountingBalances",
                table: "AccountingBalances",
                columns: new[] { "Book", "Account", "AccountName" });

            migrationBuilder.CreateTable(
                name: "AccountingOverrides",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Account = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingOverrides", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountingPeriods",
                columns: table => new
                {
                    PeriodId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Start = table.Column<long>(type: "bigint", nullable: false),
                    End = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<byte>(type: "tinyint", nullable: false),
                    ClosedAt = table.Column<long>(type: "bigint", nullable: true),
                    LastLedgerSeq = table.Column<long>(type: "bigint", nullable: false),
                    ChainHash = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    Digest = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    Signature = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    Forced = table.Column<bool>(type: "bit", nullable: false),
                    ClosingState = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingPeriods", x => x.PeriodId);
                });

            migrationBuilder.CreateTable(
                name: "AccountingPrices",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Currency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    Time = table.Column<long>(type: "bigint", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(28,8)", precision: 28, scale: 8, nullable: false),
                    Source = table.Column<byte>(type: "tinyint", nullable: false),
                    FetchedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingPrices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountingRules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Kinds = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LabelPattern = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    TagKey = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    TagValue = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Counterparty = table.Column<byte[]>(type: "varbinary(33)", nullable: true),
                    OfferId = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    ChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    TargetAccount = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountingLots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    AcquiredAt = table.Column<long>(type: "bigint", nullable: false),
                    Origin = table.Column<byte>(type: "tinyint", nullable: false),
                    SourceLedgerSeq = table.Column<long>(type: "bigint", nullable: true),
                    SourceAdjustment = table.Column<int>(type: "int", nullable: false),
                    Account = table.Column<int>(type: "int", nullable: true),
                    ParentLotId = table.Column<long>(type: "bigint", nullable: true),
                    OriginalMsat = table.Column<long>(type: "bigint", nullable: false),
                    RemainingMsat = table.Column<long>(type: "bigint", nullable: false),
                    FiatCost = table.Column<decimal>(type: "decimal(28,8)", precision: 28, scale: 8, nullable: true),
                    FiatCurrency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: true),
                    PriceId = table.Column<long>(type: "bigint", nullable: true),
                    BasisEstimated = table.Column<bool>(type: "bit", nullable: false),
                    ClosedPeriodId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingLots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountingLots_AccountingPrices_PriceId",
                        column: x => x.PriceId,
                        principalTable: "AccountingPrices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AccountingLotReliefs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LotId = table.Column<long>(type: "bigint", nullable: false),
                    LedgerSeq = table.Column<long>(type: "bigint", nullable: false),
                    Adjustment = table.Column<int>(type: "int", nullable: false),
                    RelievedAt = table.Column<long>(type: "bigint", nullable: false),
                    Msat = table.Column<long>(type: "bigint", nullable: false),
                    FiatCostRelieved = table.Column<decimal>(type: "decimal(28,8)", precision: 28, scale: 8, nullable: true),
                    Proceeds = table.Column<decimal>(type: "decimal(28,8)", precision: 28, scale: 8, nullable: true),
                    ClosedPeriodId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingLotReliefs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountingLotReliefs_AccountingLots_LotId",
                        column: x => x.LotId,
                        principalTable: "AccountingLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingPostings_Book_Account_OccurredAt",
                table: "AccountingPostings",
                columns: new[] { "Book", "Account", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingPostings_PriceId_Book_OccurredAt",
                table: "AccountingPostings",
                columns: new[] { "PriceId", "Book", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEntries_Book_ClosedPeriodId",
                table: "AccountingEntries",
                columns: new[] { "Book", "ClosedPeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEntries_Book_EventKey_Adjustment",
                table: "AccountingEntries",
                columns: new[] { "Book", "EventKey", "Adjustment" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEntries_Book_OccurredAt",
                table: "AccountingEntries",
                columns: new[] { "Book", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingLotReliefs_ClosedPeriodId",
                table: "AccountingLotReliefs",
                column: "ClosedPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingLotReliefs_LedgerSeq_Adjustment",
                table: "AccountingLotReliefs",
                columns: new[] { "LedgerSeq", "Adjustment" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingLotReliefs_LotId",
                table: "AccountingLotReliefs",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingLotReliefs_RelievedAt",
                table: "AccountingLotReliefs",
                column: "RelievedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingLots_ClosedPeriodId",
                table: "AccountingLots",
                column: "ClosedPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingLots_PriceId",
                table: "AccountingLots",
                column: "PriceId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingLots_RemainingMsat_AcquiredAt",
                table: "AccountingLots",
                columns: new[] { "RemainingMsat", "AcquiredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingLots_SourceLedgerSeq_SourceAdjustment",
                table: "AccountingLots",
                columns: new[] { "SourceLedgerSeq", "SourceAdjustment" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingOverrides_EventKey",
                table: "AccountingOverrides",
                column: "EventKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountingPeriods_State_End",
                table: "AccountingPeriods",
                columns: new[] { "State", "End" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingPrices_Currency_Time",
                table: "AccountingPrices",
                columns: new[] { "Currency", "Time" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountingRules_Priority_Id",
                table: "AccountingRules",
                columns: new[] { "Priority", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_AccountingPostings_AccountingEntries_Book_LedgerSeq_Adjustment",
                table: "AccountingPostings",
                columns: new[] { "Book", "LedgerSeq", "Adjustment" },
                principalTable: "AccountingEntries",
                principalColumns: new[] { "Book", "LedgerSeq", "Adjustment" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AccountingPostings_AccountingPrices_PriceId",
                table: "AccountingPostings",
                column: "PriceId",
                principalTable: "AccountingPrices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The older schema holds the operational book only: the financial book's rows go first (they would
            // collide on the older keys)
            migrationBuilder.Sql("DELETE FROM [AccountingPostings] WHERE [Book] <> 0;");
            migrationBuilder.Sql("DELETE FROM [AccountingEntries] WHERE [Book] <> 0;");
            migrationBuilder.Sql("DELETE FROM [AccountingBalances] WHERE [Book] <> 0;");
            migrationBuilder.Sql("DELETE FROM [AccountingCursor] WHERE [Book] <> 0;");

            migrationBuilder.DropForeignKey(
                name: "FK_AccountingPostings_AccountingEntries_Book_LedgerSeq_Adjustment",
                table: "AccountingPostings");

            migrationBuilder.DropForeignKey(
                name: "FK_AccountingPostings_AccountingPrices_PriceId",
                table: "AccountingPostings");

            migrationBuilder.DropTable(
                name: "AccountingLotReliefs");

            migrationBuilder.DropTable(
                name: "AccountingOverrides");

            migrationBuilder.DropTable(
                name: "AccountingPeriods");

            migrationBuilder.DropTable(
                name: "AccountingRules");

            migrationBuilder.DropTable(
                name: "AccountingLots");

            migrationBuilder.DropTable(
                name: "AccountingPrices");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AccountingPostings",
                table: "AccountingPostings");

            migrationBuilder.DropIndex(
                name: "IX_AccountingPostings_Book_Account_OccurredAt",
                table: "AccountingPostings");

            migrationBuilder.DropIndex(
                name: "IX_AccountingPostings_PriceId_Book_OccurredAt",
                table: "AccountingPostings");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AccountingEntries",
                table: "AccountingEntries");

            migrationBuilder.DropIndex(
                name: "IX_AccountingEntries_Book_ClosedPeriodId",
                table: "AccountingEntries");

            migrationBuilder.DropIndex(
                name: "IX_AccountingEntries_Book_EventKey_Adjustment",
                table: "AccountingEntries");

            migrationBuilder.DropIndex(
                name: "IX_AccountingEntries_Book_OccurredAt",
                table: "AccountingEntries");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AccountingCursor",
                table: "AccountingCursor");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AccountingBalances",
                table: "AccountingBalances");

            migrationBuilder.DropColumn(
                name: "Label",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "Label",
                table: "Offers");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "Offers");

            migrationBuilder.DropColumn(
                name: "Label",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Label",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "Label",
                table: "BroadcastTransactions");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "BroadcastTransactions");

            migrationBuilder.DropColumn(
                name: "Book",
                table: "AccountingPostings");

            migrationBuilder.DropColumn(
                name: "Adjustment",
                table: "AccountingPostings");

            migrationBuilder.DropColumn(
                name: "AccountName",
                table: "AccountingPostings");

            migrationBuilder.DropColumn(
                name: "FiatAmount",
                table: "AccountingPostings");

            migrationBuilder.DropColumn(
                name: "FiatCurrency",
                table: "AccountingPostings");

            migrationBuilder.DropColumn(
                name: "PriceId",
                table: "AccountingPostings");

            migrationBuilder.DropColumn(
                name: "Book",
                table: "AccountingEntries");

            migrationBuilder.DropColumn(
                name: "Adjustment",
                table: "AccountingEntries");

            migrationBuilder.DropColumn(
                name: "Classification",
                table: "AccountingEntries");

            migrationBuilder.DropColumn(
                name: "ClosedPeriodId",
                table: "AccountingEntries");

            migrationBuilder.DropColumn(
                name: "Flags",
                table: "AccountingEntries");

            migrationBuilder.DropColumn(
                name: "RuleId",
                table: "AccountingEntries");

            migrationBuilder.DropColumn(
                name: "Book",
                table: "AccountingCursor");

            migrationBuilder.DropColumn(
                name: "Book",
                table: "AccountingBalances");

            migrationBuilder.DropColumn(
                name: "AccountName",
                table: "AccountingBalances");

            migrationBuilder.DropColumn(
                name: "FiatAmount",
                table: "AccountingBalances");

            migrationBuilder.AddColumn<int>(
                name: "Id",
                table: "AccountingCursor",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // The operational cursor row is the A2 singleton again (Id 1)
            migrationBuilder.Sql("UPDATE [AccountingCursor] SET [Id] = 1;");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AccountingPostings",
                table: "AccountingPostings",
                columns: new[] { "LedgerSeq", "Index" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_AccountingEntries",
                table: "AccountingEntries",
                column: "LedgerSeq");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AccountingCursor",
                table: "AccountingCursor",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AccountingBalances",
                table: "AccountingBalances",
                column: "Account");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingPostings_Account_OccurredAt",
                table: "AccountingPostings",
                columns: new[] { "Account", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEntries_EventKey",
                table: "AccountingEntries",
                column: "EventKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountingEntries_OccurredAt",
                table: "AccountingEntries",
                column: "OccurredAt");

            migrationBuilder.AddForeignKey(
                name: "FK_AccountingPostings_AccountingEntries_LedgerSeq",
                table: "AccountingPostings",
                column: "LedgerSeq",
                principalTable: "AccountingEntries",
                principalColumn: "LedgerSeq",
                onDelete: ReferentialAction.Cascade);
        }
    }
}