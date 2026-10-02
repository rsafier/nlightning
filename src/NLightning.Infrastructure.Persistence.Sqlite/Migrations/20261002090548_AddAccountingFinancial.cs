using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountingFinancial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Id",
                table: "AccountingCursor",
                newName: "Book");

            // The A2 singleton (Id 1) is the operational book's cursor row (Book 0)
            migrationBuilder.Sql("UPDATE \"AccountingCursor\" SET \"Book\" = 0;");

            migrationBuilder.AddColumn<string>(
                name: "Label",
                table: "Payments",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "Payments",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Label",
                table: "Offers",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "Offers",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Label",
                table: "Invoices",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "Invoices",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Label",
                table: "Channels",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "Channels",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Label",
                table: "BroadcastTransactions",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "BroadcastTransactions",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccountingOverrides",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Account = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingOverrides", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountingPeriods",
                columns: table => new
                {
                    PeriodId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Start = table.Column<long>(type: "INTEGER", nullable: false),
                    End = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<byte>(type: "INTEGER", nullable: false),
                    ClosedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastLedgerSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    ChainHash = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Digest = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Signature = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Forced = table.Column<bool>(type: "INTEGER", nullable: false),
                    ClosingState = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingPeriods", x => x.PeriodId);
                });

            migrationBuilder.CreateTable(
                name: "AccountingPrices",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Currency = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 3, nullable: false),
                    Time = table.Column<long>(type: "INTEGER", nullable: false),
                    Price = table.Column<decimal>(type: "TEXT", precision: 28, scale: 8, nullable: false),
                    Source = table.Column<byte>(type: "INTEGER", nullable: false),
                    FetchedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingPrices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountingRules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Kinds = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    LabelPattern = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    TagKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    TagValue = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Counterparty = table.Column<byte[]>(type: "BLOB", nullable: true),
                    OfferId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    TargetAccount = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountingLots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    AcquiredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Origin = table.Column<byte>(type: "INTEGER", nullable: false),
                    SourceLedgerSeq = table.Column<long>(type: "INTEGER", nullable: true),
                    SourceAdjustment = table.Column<int>(type: "INTEGER", nullable: false),
                    Account = table.Column<int>(type: "INTEGER", nullable: true),
                    ParentLotId = table.Column<long>(type: "INTEGER", nullable: true),
                    OriginalMsat = table.Column<long>(type: "INTEGER", nullable: false),
                    RemainingMsat = table.Column<long>(type: "INTEGER", nullable: false),
                    FiatCost = table.Column<decimal>(type: "TEXT", precision: 28, scale: 8, nullable: true),
                    FiatCurrency = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 3, nullable: true),
                    PriceId = table.Column<long>(type: "INTEGER", nullable: true),
                    BasisEstimated = table.Column<bool>(type: "INTEGER", nullable: false),
                    ClosedPeriodId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
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
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LotId = table.Column<long>(type: "INTEGER", nullable: false),
                    LedgerSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    Adjustment = table.Column<int>(type: "INTEGER", nullable: false),
                    RelievedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Msat = table.Column<long>(type: "INTEGER", nullable: false),
                    FiatCostRelieved = table.Column<decimal>(type: "TEXT", precision: 28, scale: 8, nullable: true),
                    Proceeds = table.Column<decimal>(type: "TEXT", precision: 28, scale: 8, nullable: true),
                    ClosedPeriodId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
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

            // SQLite cannot change a primary key or add a foreign key in place, so the books' three tables are rebuilt.
            // The rebuild is written by hand (as RemoveShadowForeignKeyColumns did, NL-134): EF's generic rebuild would
            // recreate them without the defaults of the new columns, which the other providers keep. The A2 rows are
            // the operational book's (Book 0, Adjustment 0).
            migrationBuilder.Sql("PRAGMA foreign_keys=0;", suppressTransaction: true);
            migrationBuilder.Sql("""
                CREATE TABLE "ef_temp_AccountingEntries" (
                    "Book" INTEGER NOT NULL DEFAULT 0,
                    "LedgerSeq" INTEGER NOT NULL,
                    "Adjustment" INTEGER NOT NULL DEFAULT 0,
                    "EventKey" TEXT NOT NULL,
                    "Kind" INTEGER NOT NULL,
                    "OccurredAt" INTEGER NOT NULL,
                    "ChannelId" BLOB NULL,
                    "PaymentHash" BLOB NULL,
                    "Note" TEXT NULL,
                    "Flags" INTEGER NOT NULL DEFAULT 0,
                    "Classification" INTEGER NULL,
                    "RuleId" INTEGER NULL,
                    "ClosedPeriodId" TEXT NULL,
                    CONSTRAINT "PK_AccountingEntries" PRIMARY KEY ("Book", "LedgerSeq", "Adjustment")
                );
                INSERT INTO "ef_temp_AccountingEntries" ("LedgerSeq", "EventKey", "Kind", "OccurredAt", "ChannelId", "PaymentHash", "Note")
                SELECT "LedgerSeq", "EventKey", "Kind", "OccurredAt", "ChannelId", "PaymentHash", "Note"
                FROM "AccountingEntries";
                CREATE TABLE "ef_temp_AccountingPostings" (
                    "Book" INTEGER NOT NULL DEFAULT 0,
                    "LedgerSeq" INTEGER NOT NULL,
                    "Adjustment" INTEGER NOT NULL DEFAULT 0,
                    "Index" INTEGER NOT NULL,
                    "Account" INTEGER NOT NULL,
                    "AccountName" TEXT NULL,
                    "AmountMsat" INTEGER NOT NULL,
                    "OccurredAt" INTEGER NOT NULL,
                    "FiatAmount" TEXT NULL,
                    "FiatCurrency" TEXT NULL,
                    "PriceId" INTEGER NULL,
                    CONSTRAINT "PK_AccountingPostings" PRIMARY KEY ("Book", "LedgerSeq", "Adjustment", "Index"),
                    CONSTRAINT "FK_AccountingPostings_AccountingEntries_Book_LedgerSeq_Adjustment" FOREIGN KEY ("Book", "LedgerSeq", "Adjustment") REFERENCES "AccountingEntries" ("Book", "LedgerSeq", "Adjustment") ON DELETE CASCADE,
                    CONSTRAINT "FK_AccountingPostings_AccountingPrices_PriceId" FOREIGN KEY ("PriceId") REFERENCES "AccountingPrices" ("Id") ON DELETE RESTRICT
                );
                INSERT INTO "ef_temp_AccountingPostings" ("LedgerSeq", "Index", "Account", "AmountMsat", "OccurredAt")
                SELECT "LedgerSeq", "Index", "Account", "AmountMsat", "OccurredAt"
                FROM "AccountingPostings";
                CREATE TABLE "ef_temp_AccountingBalances" (
                    "Book" INTEGER NOT NULL DEFAULT 0,
                    "Account" INTEGER NOT NULL,
                    "AccountName" TEXT NOT NULL DEFAULT '',
                    "BalanceMsat" INTEGER NOT NULL,
                    "FiatAmount" TEXT NOT NULL DEFAULT '0.0',
                    CONSTRAINT "PK_AccountingBalances" PRIMARY KEY ("Book", "Account", "AccountName")
                );
                INSERT INTO "ef_temp_AccountingBalances" ("Account", "BalanceMsat")
                SELECT "Account", "BalanceMsat"
                FROM "AccountingBalances";
                DROP TABLE "AccountingPostings";
                DROP TABLE "AccountingEntries";
                DROP TABLE "AccountingBalances";
                ALTER TABLE "ef_temp_AccountingEntries" RENAME TO "AccountingEntries";
                ALTER TABLE "ef_temp_AccountingPostings" RENAME TO "AccountingPostings";
                ALTER TABLE "ef_temp_AccountingBalances" RENAME TO "AccountingBalances";
                CREATE INDEX "IX_AccountingEntries_Book_ClosedPeriodId" ON "AccountingEntries" ("Book", "ClosedPeriodId");
                CREATE UNIQUE INDEX "IX_AccountingEntries_Book_EventKey_Adjustment" ON "AccountingEntries" ("Book", "EventKey", "Adjustment");
                CREATE INDEX "IX_AccountingEntries_Book_OccurredAt" ON "AccountingEntries" ("Book", "OccurredAt");
                CREATE INDEX "IX_AccountingPostings_Book_Account_OccurredAt" ON "AccountingPostings" ("Book", "Account", "OccurredAt");
                CREATE INDEX "IX_AccountingPostings_PriceId_Book_OccurredAt" ON "AccountingPostings" ("PriceId", "Book", "OccurredAt");
                """);
            migrationBuilder.Sql("PRAGMA foreign_keys=1;", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Written by hand like the Up rebuild: EF's generic SQLite rebuilds would recreate Channels (and the other
            // tables that lose their label and tags) without the column defaults earlier migrations added (NL-134),
            // and would rebuild the postings against the entries' new key. The older schema holds the operational
            // book only: the financial book's rows go first (they would collide on the older keys).
            migrationBuilder.Sql("""
                DELETE FROM "AccountingPostings" WHERE "Book" <> 0;
                DELETE FROM "AccountingEntries" WHERE "Book" <> 0;
                DELETE FROM "AccountingBalances" WHERE "Book" <> 0;
                DELETE FROM "AccountingCursor" WHERE "Book" <> 0;
                DROP TABLE "AccountingLotReliefs";
                DROP TABLE "AccountingLots";
                DROP TABLE "AccountingOverrides";
                DROP TABLE "AccountingPeriods";
                DROP TABLE "AccountingRules";
                ALTER TABLE "Payments" DROP COLUMN "Label";
                ALTER TABLE "Payments" DROP COLUMN "Tags";
                ALTER TABLE "Offers" DROP COLUMN "Label";
                ALTER TABLE "Offers" DROP COLUMN "Tags";
                ALTER TABLE "Invoices" DROP COLUMN "Label";
                ALTER TABLE "Invoices" DROP COLUMN "Tags";
                ALTER TABLE "Channels" DROP COLUMN "Label";
                ALTER TABLE "Channels" DROP COLUMN "Tags";
                ALTER TABLE "BroadcastTransactions" DROP COLUMN "Label";
                ALTER TABLE "BroadcastTransactions" DROP COLUMN "Tags";
                ALTER TABLE "AccountingCursor" RENAME COLUMN "Book" TO "Id";
                UPDATE "AccountingCursor" SET "Id" = 1;
                """);
            migrationBuilder.Sql("PRAGMA foreign_keys=0;", suppressTransaction: true);
            migrationBuilder.Sql("""
                CREATE TABLE "ef_temp_AccountingEntries" (
                    "LedgerSeq" INTEGER NOT NULL CONSTRAINT "PK_AccountingEntries" PRIMARY KEY,
                    "EventKey" TEXT NOT NULL,
                    "Kind" INTEGER NOT NULL,
                    "OccurredAt" INTEGER NOT NULL,
                    "ChannelId" BLOB NULL,
                    "PaymentHash" BLOB NULL,
                    "Note" TEXT NULL
                );
                INSERT INTO "ef_temp_AccountingEntries" ("LedgerSeq", "EventKey", "Kind", "OccurredAt", "ChannelId", "PaymentHash", "Note")
                SELECT "LedgerSeq", "EventKey", "Kind", "OccurredAt", "ChannelId", "PaymentHash", "Note"
                FROM "AccountingEntries";
                CREATE TABLE "ef_temp_AccountingPostings" (
                    "LedgerSeq" INTEGER NOT NULL,
                    "Index" INTEGER NOT NULL,
                    "Account" INTEGER NOT NULL,
                    "AmountMsat" INTEGER NOT NULL,
                    "OccurredAt" INTEGER NOT NULL,
                    CONSTRAINT "PK_AccountingPostings" PRIMARY KEY ("LedgerSeq", "Index"),
                    CONSTRAINT "FK_AccountingPostings_AccountingEntries_LedgerSeq" FOREIGN KEY ("LedgerSeq") REFERENCES "AccountingEntries" ("LedgerSeq") ON DELETE CASCADE
                );
                INSERT INTO "ef_temp_AccountingPostings" ("LedgerSeq", "Index", "Account", "AmountMsat", "OccurredAt")
                SELECT "LedgerSeq", "Index", "Account", "AmountMsat", "OccurredAt"
                FROM "AccountingPostings";
                CREATE TABLE "ef_temp_AccountingBalances" (
                    "Account" INTEGER NOT NULL CONSTRAINT "PK_AccountingBalances" PRIMARY KEY,
                    "BalanceMsat" INTEGER NOT NULL
                );
                INSERT INTO "ef_temp_AccountingBalances" ("Account", "BalanceMsat")
                SELECT "Account", "BalanceMsat"
                FROM "AccountingBalances";
                DROP TABLE "AccountingPostings";
                DROP TABLE "AccountingEntries";
                DROP TABLE "AccountingBalances";
                DROP TABLE "AccountingPrices";
                ALTER TABLE "ef_temp_AccountingEntries" RENAME TO "AccountingEntries";
                ALTER TABLE "ef_temp_AccountingPostings" RENAME TO "AccountingPostings";
                ALTER TABLE "ef_temp_AccountingBalances" RENAME TO "AccountingBalances";
                CREATE UNIQUE INDEX "IX_AccountingEntries_EventKey" ON "AccountingEntries" ("EventKey");
                CREATE INDEX "IX_AccountingEntries_OccurredAt" ON "AccountingEntries" ("OccurredAt");
                CREATE INDEX "IX_AccountingPostings_Account_OccurredAt" ON "AccountingPostings" ("Account", "OccurredAt");
                """);
            migrationBuilder.Sql("PRAGMA foreign_keys=1;", suppressTransaction: true);
        }
    }
}