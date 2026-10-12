using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddSilentPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "IsAddressChange",
                table: "Utxos",
                type: "bit",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "bit");

            migrationBuilder.AlterColumn<byte>(
                name: "AddressType",
                table: "Utxos",
                type: "tinyint",
                nullable: true,
                oldClrType: typeof(byte),
                oldType: "tinyint");

            migrationBuilder.AlterColumn<long>(
                name: "AddressIndex",
                table: "Utxos",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<long>(
                name: "SilentPaymentIndex",
                table: "Utxos",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "SilentPaymentTransactionId",
                table: "Utxos",
                type: "varbinary(32)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SilentPaymentLabels",
                columns: table => new
                {
                    M = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAtHeight = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SilentPaymentLabels", x => x.M);
                });

            migrationBuilder.CreateTable(
                name: "SilentPaymentOutputs",
                columns: table => new
                {
                    TransactionId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    Index = table.Column<long>(type: "bigint", nullable: false),
                    OutputKey = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    Tweak = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    Label = table.Column<long>(type: "bigint", nullable: true),
                    AmountSats = table.Column<long>(type: "bigint", nullable: false),
                    BlockHeight = table.Column<long>(type: "bigint", nullable: false),
                    BlockHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    SpentByTransactionId = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    SpentAtHeight = table.Column<long>(type: "bigint", nullable: true),
                    Ignored = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SilentPaymentOutputs", x => new { x.TransactionId, x.Index });
                });

            migrationBuilder.CreateTable(
                name: "SilentPaymentScanState",
                columns: table => new
                {
                    Id = table.Column<byte>(type: "tinyint", nullable: false),
                    BirthdayHeight = table.Column<long>(type: "bigint", nullable: false),
                    LiveFromHeight = table.Column<long>(type: "bigint", nullable: false),
                    RescanCursorHeight = table.Column<long>(type: "bigint", nullable: true),
                    RescanCursorHash = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    RescanTargetHeight = table.Column<long>(type: "bigint", nullable: true),
                    RecoveryLabelCount = table.Column<long>(type: "bigint", nullable: false),
                    LiveCursorHeight = table.Column<long>(type: "bigint", nullable: true),
                    LiveCursorHash = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    PrevoutSource = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SilentPaymentScanState", x => x.Id);
                    table.CheckConstraint("CK_SilentPaymentScanState_Singleton", "[Id] = 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Utxos_SilentPaymentTransactionId_SilentPaymentIndex",
                table: "Utxos",
                columns: new[] { "SilentPaymentTransactionId", "SilentPaymentIndex" },
                unique: true,
                filter: "[SilentPaymentTransactionId] IS NOT NULL AND [SilentPaymentIndex] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Utxos_Ownership",
                table: "Utxos",
                sql: "(([AddressIndex] IS NOT NULL AND [IsAddressChange] IS NOT NULL AND [AddressType] IS NOT NULL AND [SilentPaymentTransactionId] IS NULL AND [SilentPaymentIndex] IS NULL) OR ([AddressIndex] IS NULL AND [IsAddressChange] IS NULL AND [AddressType] IS NULL AND [SilentPaymentTransactionId] IS NOT NULL AND [SilentPaymentIndex] IS NOT NULL AND [SilentPaymentTransactionId] = [TransactionId] AND [SilentPaymentIndex] = [Index]))");

            migrationBuilder.CreateIndex(
                name: "IX_SilentPaymentLabels_Name",
                table: "SilentPaymentLabels",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SilentPaymentOutputs_BlockHeight",
                table: "SilentPaymentOutputs",
                column: "BlockHeight");

            migrationBuilder.CreateIndex(
                name: "IX_SilentPaymentOutputs_SpentByTransactionId",
                table: "SilentPaymentOutputs",
                column: "SpentByTransactionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Utxos_SilentPaymentOutputs_SilentPaymentTransactionId_SilentPaymentIndex",
                table: "Utxos",
                columns: new[] { "SilentPaymentTransactionId", "SilentPaymentIndex" },
                principalTable: "SilentPaymentOutputs",
                principalColumns: new[] { "TransactionId", "Index" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Utxos_SilentPaymentOutputs_SilentPaymentTransactionId_SilentPaymentIndex",
                table: "Utxos");

            migrationBuilder.DropTable(
                name: "SilentPaymentLabels");

            migrationBuilder.DropTable(
                name: "SilentPaymentOutputs");

            migrationBuilder.DropTable(
                name: "SilentPaymentScanState");

            migrationBuilder.DropIndex(
                name: "IX_Utxos_SilentPaymentTransactionId_SilentPaymentIndex",
                table: "Utxos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Utxos_Ownership",
                table: "Utxos");

            migrationBuilder.DropColumn(
                name: "SilentPaymentIndex",
                table: "Utxos");

            migrationBuilder.DropColumn(
                name: "SilentPaymentTransactionId",
                table: "Utxos");

            migrationBuilder.AlterColumn<bool>(
                name: "IsAddressChange",
                table: "Utxos",
                type: "bit",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "bit",
                oldNullable: true);

            migrationBuilder.AlterColumn<byte>(
                name: "AddressType",
                table: "Utxos",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0,
                oldClrType: typeof(byte),
                oldType: "tinyint",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "AddressIndex",
                table: "Utxos",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);
        }
    }
}