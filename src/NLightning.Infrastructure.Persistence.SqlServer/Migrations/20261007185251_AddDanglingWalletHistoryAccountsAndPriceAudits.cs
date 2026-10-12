using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
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
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AccountIndex",
                table: "WalletAddresses",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "AccountName",
                table: "WalletAddresses",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "default");

            migrationBuilder.AddColumn<long>(
                name: "DerivationIndex",
                table: "WalletAddresses",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ActualIncomingAmountMsat",
                table: "ForwardCircuits",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "IncomingClaimedPreimage",
                table: "ForwardCircuits",
                type: "varbinary(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccountingPriceReplacementAudits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PriceId = table.Column<long>(type: "bigint", nullable: false),
                    OldPrice = table.Column<decimal>(type: "decimal(28,8)", precision: 28, scale: 8, nullable: false),
                    NewPrice = table.Column<decimal>(type: "decimal(28,8)", precision: 28, scale: 8, nullable: false),
                    OldSource = table.Column<byte>(type: "tinyint", nullable: false),
                    NewSource = table.Column<byte>(type: "tinyint", nullable: false),
                    OldFetchedAt = table.Column<long>(type: "bigint", nullable: false),
                    ReplacedAt = table.Column<long>(type: "bigint", nullable: false),
                    OperatorSource = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true)
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
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AddressType = table.Column<byte>(type: "tinyint", nullable: false),
                    AccountIndex = table.Column<long>(type: "bigint", nullable: false),
                    ExtendedPublicKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    MasterFingerprint = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    DerivationPath = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    WatchOnly = table.Column<bool>(type: "bit", nullable: false),
                    BirthdayHeight = table.Column<long>(type: "bigint", nullable: false),
                    ExternalKeyCount = table.Column<long>(type: "bigint", nullable: false),
                    InternalKeyCount = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletAccounts", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "WalletHistoryRescanStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Generation = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedFromHeight = table.Column<long>(type: "bigint", nullable: false),
                    AvailableFromHeight = table.Column<long>(type: "bigint", nullable: false),
                    TargetHeight = table.Column<long>(type: "bigint", nullable: false),
                    CursorHeight = table.Column<long>(type: "bigint", nullable: true),
                    CursorHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: true),
                    AddressCount = table.Column<long>(type: "bigint", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsPartial = table.Column<bool>(type: "bit", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletHistoryRescanStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WalletTransactionLabels",
                columns: table => new
                {
                    TransactionId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false)
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