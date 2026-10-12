using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddDanglingWalletHistoryAccountsAndPriceAudits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnershipSummary",
                table: "WalletTransactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "AccountIndex",
                table: "WalletAddresses",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<string>(
                name: "AccountName",
                table: "WalletAddresses",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "default");

            migrationBuilder.AddColumn<uint>(
                name: "DerivationIndex",
                table: "WalletAddresses",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ActualIncomingAmountMsat",
                table: "ForwardCircuits",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "IncomingClaimedPreimage",
                table: "ForwardCircuits",
                type: "BLOB",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccountingPriceReplacementAudits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PriceId = table.Column<long>(type: "INTEGER", nullable: false),
                    OldPrice = table.Column<decimal>(type: "TEXT", precision: 28, scale: 8, nullable: false),
                    NewPrice = table.Column<decimal>(type: "TEXT", precision: 28, scale: 8, nullable: false),
                    OldSource = table.Column<byte>(type: "INTEGER", nullable: false),
                    NewSource = table.Column<byte>(type: "INTEGER", nullable: false),
                    OldFetchedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ReplacedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OperatorSource = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingPriceReplacementAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountingPriceReplacementAudits_AccountingPrices_PriceId",
                        column: x => x.PriceId,
                        principalTable: "AccountingPrices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WalletAccounts",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AddressType = table.Column<byte>(type: "INTEGER", nullable: false),
                    AccountIndex = table.Column<uint>(type: "INTEGER", nullable: false),
                    ExtendedPublicKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    MasterFingerprint = table.Column<byte[]>(type: "BLOB", nullable: false),
                    DerivationPath = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    WatchOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    BirthdayHeight = table.Column<uint>(type: "INTEGER", nullable: false),
                    ExternalKeyCount = table.Column<uint>(type: "INTEGER", nullable: false),
                    InternalKeyCount = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletAccounts", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "WalletHistoryRescanStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Generation = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestedFromHeight = table.Column<uint>(type: "INTEGER", nullable: false),
                    AvailableFromHeight = table.Column<uint>(type: "INTEGER", nullable: false),
                    TargetHeight = table.Column<uint>(type: "INTEGER", nullable: false),
                    CursorHeight = table.Column<uint>(type: "INTEGER", nullable: true),
                    CursorHash = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: true),
                    AddressCount = table.Column<uint>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsPartial = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletHistoryRescanStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WalletTransactionLabels",
                columns: table => new
                {
                    TransactionId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletTransactionLabels", x => x.TransactionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingPriceReplacementAudits_PriceId_ReplacedAt_Id",
                table: "AccountingPriceReplacementAudits",
                columns: new[] { "PriceId", "ReplacedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountingPriceReplacementAudits");

            migrationBuilder.DropTable(
                name: "WalletAccounts");

            migrationBuilder.DropTable(
                name: "WalletHistoryRescanStates");

            migrationBuilder.DropTable(
                name: "WalletTransactionLabels");

            migrationBuilder.DropColumn(
                name: "OwnershipSummary",
                table: "WalletTransactions");

            migrationBuilder.DropColumn(
                name: "AccountIndex",
                table: "WalletAddresses");

            migrationBuilder.DropColumn(
                name: "AccountName",
                table: "WalletAddresses");

            migrationBuilder.DropColumn(
                name: "DerivationIndex",
                table: "WalletAddresses");

            migrationBuilder.DropColumn(
                name: "ActualIncomingAmountMsat",
                table: "ForwardCircuits");

            migrationBuilder.DropColumn(
                name: "IncomingClaimedPreimage",
                table: "ForwardCircuits");
        }
    }
}