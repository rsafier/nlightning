using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddSpliceFundings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_RevokedCommitments",
                table: "RevokedCommitments");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Commitments",
                table: "Commitments");

            migrationBuilder.AddColumn<byte[]>(
                name: "FundingTxId",
                table: "RevokedCommitments",
                type: "varbinary(32)",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "FundingTxId",
                table: "Commitments",
                type: "varbinary(32)",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<bool>(
                name: "IsDualFunded",
                table: "Channels",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "LocalFundingContributionSatoshis",
                table: "Channels",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RemoteFundingContributionSatoshis",
                table: "Channels",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_RevokedCommitments",
                table: "RevokedCommitments",
                columns: new[] { "ChannelId", "Number", "FundingTxId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_Commitments",
                table: "Commitments",
                columns: new[] { "ChannelId", "Slot", "FundingTxId" });

            migrationBuilder.CreateTable(
                name: "ChannelFundings",
                columns: table => new
                {
                    ChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    FundingTxId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    OutputIndex = table.Column<int>(type: "int", nullable: false),
                    CapacitySatoshis = table.Column<long>(type: "bigint", nullable: false),
                    LocalFundingPubKey = table.Column<byte[]>(type: "varbinary(33)", nullable: false),
                    RemoteFundingPubKey = table.Column<byte[]>(type: "varbinary(33)", nullable: false),
                    LocalFundingKeyIndex = table.Column<long>(type: "bigint", nullable: false),
                    LocalBalanceDeltaMsat = table.Column<long>(type: "bigint", nullable: false),
                    RemoteBalanceDeltaMsat = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    FeeratePerKw = table.Column<long>(type: "bigint", nullable: true),
                    Locktime = table.Column<long>(type: "bigint", nullable: true),
                    RbfOf = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    ConfirmedHeight = table.Column<long>(type: "bigint", nullable: true),
                    ShortChannelId = table.Column<byte[]>(type: "varbinary(8)", nullable: true),
                    SpliceLockedSent = table.Column<bool>(type: "bit", nullable: false),
                    SpliceLockedReceived = table.Column<bool>(type: "bit", nullable: false),
                    AnnouncementSignaturesReceived = table.Column<bool>(type: "bit", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelFundings", x => new { x.ChannelId, x.FundingTxId });
                    table.ForeignKey(
                        name: "FK_ChannelFundings_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "ChannelId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChannelPolicies",
                columns: table => new
                {
                    ChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    FeeBaseMsat = table.Column<long>(type: "bigint", nullable: true),
                    FeeProportionalMillionths = table.Column<long>(type: "bigint", nullable: true),
                    CltvExpiryDelta = table.Column<int>(type: "int", nullable: true),
                    HtlcMinimumMsat = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    HtlcMaximumMsat = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelPolicies", x => x.ChannelId);
                });

            // ---- Hand-written data step (splicing plan §3.8, lane SP1-C): not generated by EF ----
            // Existing commitment rows and revocation-log rows spend the channel's current funding: fill their new
            // FundingTxId key column from Channels.FundingTxId. Then give every channel with a known funding outpoint
            // its Current, Initial ChannelFundings row (funding key index 0, no deltas, both funding keys from the key
            // sets, the confirmed short channel id if any).
            migrationBuilder.Sql("UPDATE m SET m.[FundingTxId] = c.[FundingTxId] FROM [Commitments] AS m INNER JOIN [Channels] AS c ON c.[ChannelId] = m.[ChannelId];");
            migrationBuilder.Sql("UPDATE m SET m.[FundingTxId] = c.[FundingTxId] FROM [RevokedCommitments] AS m INNER JOIN [Channels] AS c ON c.[ChannelId] = m.[ChannelId];");
            migrationBuilder.Sql("INSERT INTO [ChannelFundings] ([ChannelId], [FundingTxId], [OutputIndex], [CapacitySatoshis], [LocalFundingPubKey], [RemoteFundingPubKey], [LocalFundingKeyIndex], [LocalBalanceDeltaMsat], [RemoteBalanceDeltaMsat], [Kind], [Status], [ShortChannelId], [SpliceLockedSent], [SpliceLockedReceived], [AnnouncementSignaturesReceived], [Sequence]) SELECT c.[ChannelId], c.[FundingTxId], c.[FundingOutputIndex], c.[FundingAmountSatoshis], l.[FundingPubKey], r.[FundingPubKey], 0, 0, 0, 0, 1, c.[ShortChannelId], 0, 0, 0, 0 FROM [Channels] AS c INNER JOIN [ChannelKeySets] AS l ON l.[ChannelId] = c.[ChannelId] AND l.[IsLocal] = 1 INNER JOIN [ChannelKeySets] AS r ON r.[ChannelId] = c.[ChannelId] AND r.[IsLocal] = 0 WHERE c.[FundingTxId] <> 0x0000000000000000000000000000000000000000000000000000000000000000;");
            // ---- End of the hand-written data step ----
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ---- Hand-written data step: before the old keys come back, keep only the rows of the current funding ----
            migrationBuilder.Sql("DELETE m FROM [Commitments] AS m INNER JOIN [Channels] AS c ON c.[ChannelId] = m.[ChannelId] WHERE m.[FundingTxId] <> c.[FundingTxId];");
            migrationBuilder.Sql("DELETE m FROM [RevokedCommitments] AS m INNER JOIN [Channels] AS c ON c.[ChannelId] = m.[ChannelId] WHERE m.[FundingTxId] <> c.[FundingTxId];");
            // ---- End of the hand-written data step ----

            migrationBuilder.DropTable(
                name: "ChannelFundings");

            migrationBuilder.DropTable(
                name: "ChannelPolicies");

            migrationBuilder.DropPrimaryKey(
                name: "PK_RevokedCommitments",
                table: "RevokedCommitments");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Commitments",
                table: "Commitments");

            migrationBuilder.DropColumn(
                name: "FundingTxId",
                table: "RevokedCommitments");

            migrationBuilder.DropColumn(
                name: "FundingTxId",
                table: "Commitments");

            migrationBuilder.DropColumn(
                name: "IsDualFunded",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "LocalFundingContributionSatoshis",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "RemoteFundingContributionSatoshis",
                table: "Channels");

            migrationBuilder.AddPrimaryKey(
                name: "PK_RevokedCommitments",
                table: "RevokedCommitments",
                columns: new[] { "ChannelId", "Number" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_Commitments",
                table: "Commitments",
                columns: new[] { "ChannelId", "Slot" });
        }
    }
}