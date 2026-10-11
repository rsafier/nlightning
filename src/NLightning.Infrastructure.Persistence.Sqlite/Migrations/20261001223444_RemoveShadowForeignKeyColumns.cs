using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class RemoveShadowForeignKeyColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Channels_PeerEntityNodeId",
                table: "Channels");

            if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                // SQLite cannot drop a foreign-key column directly (the FK lives in the table definition), so the
                // table must be rebuilt - but EF's generic rebuild recreates Channels without the column defaults
                // earlier migrations added (IsDualFunded, DataLossDetected, the balances and commitment numbers), and
                // raw seeds that omit them fail the NOT NULL constraints. The rebuild is done here by hand instead,
                // keeping every default.
                migrationBuilder.Sql("PRAGMA foreign_keys=0;", suppressTransaction: true);
                migrationBuilder.Sql("""
                    CREATE TABLE "ef_temp_Channels" (
                        "ChannelId" BLOB NOT NULL CONSTRAINT "PK_Channels" PRIMARY KEY,
                        "ChangeAddressAddressType" INTEGER NULL,
                        "ChangeAddressIndex" INTEGER NULL,
                        "ChangeAddressIsChange" INTEGER NULL,
                        "ClosingTransaction" BLOB NULL,
                        "ClosingTxId" BLOB NULL,
                        "DataLossDetected" INTEGER NOT NULL DEFAULT 0,
                        "ErrorSent" BLOB NULL,
                        "FirstRemoteHtlcIdAfterLocalShutdown" INTEGER NULL,
                        "FundingAmountSatoshis" INTEGER NOT NULL,
                        "FundingCreatedAtBlockHeight" INTEGER NOT NULL,
                        "FundingOutputIndex" INTEGER NOT NULL,
                        "FundingTxId" BLOB NOT NULL,
                        "IsDualFunded" INTEGER NOT NULL DEFAULT 0,
                        "IsInitiator" INTEGER NOT NULL,
                        "LastReceivedSignature" BLOB NULL,
                        "LastSentOrder" INTEGER NOT NULL,
                        "LastSentSignature" BLOB NULL,
                        "LocalAnnouncementSigsSentAt" INTEGER NULL,
                        "LocalBalanceMsat" INTEGER NOT NULL DEFAULT 0,
                        "LocalCommitmentNumber" INTEGER NOT NULL DEFAULT 0,
                        "LocalFundingContributionSatoshis" INTEGER NULL,
                        "LocalNextHtlcId" INTEGER NOT NULL,
                        "LocalRevocationNumber" INTEGER NOT NULL,
                        "LocalShutdownScript" BLOB NULL,
                        "MaxDustHtlcExposureMsat" INTEGER NULL,
                        "RemoteAlias" BLOB NULL,
                        "RemoteAnnouncementBitcoinSig" BLOB NULL,
                        "RemoteAnnouncementNodeSig" BLOB NULL,
                        "RemoteBalanceMsat" INTEGER NOT NULL DEFAULT 0,
                        "RemoteCommitmentNumber" INTEGER NOT NULL DEFAULT 0,
                        "RemoteFundingContributionSatoshis" INTEGER NULL,
                        "RemoteNextHtlcId" INTEGER NOT NULL,
                        "RemoteNextPerCommitmentPoint" BLOB NULL,
                        "RemoteNodeId" BLOB NOT NULL,
                        "RemoteRevocationNumber" INTEGER NOT NULL,
                        "RemoteShutdownScript" BLOB NULL,
                        "RevocationLogFromNumber" INTEGER NULL,
                        "SentCommitDiff" BLOB NULL,
                        "ShortChannelId" BLOB NULL,
                        "State" INTEGER NOT NULL,
                        "Version" INTEGER NOT NULL,
                        CONSTRAINT "FK_Channels_WalletAddresses_ChangeAddressIndex_ChangeAddressIsChange_ChangeAddressAddressType" FOREIGN KEY ("ChangeAddressIndex", "ChangeAddressIsChange", "ChangeAddressAddressType") REFERENCES "WalletAddresses" ("Index", "IsChange", "AddressType")
                    );
                    INSERT INTO "ef_temp_Channels" ("ChannelId", "ChangeAddressAddressType", "ChangeAddressIndex", "ChangeAddressIsChange", "ClosingTransaction", "ClosingTxId", "DataLossDetected", "ErrorSent", "FirstRemoteHtlcIdAfterLocalShutdown", "FundingAmountSatoshis", "FundingCreatedAtBlockHeight", "FundingOutputIndex", "FundingTxId", "IsDualFunded", "IsInitiator", "LastReceivedSignature", "LastSentOrder", "LastSentSignature", "LocalAnnouncementSigsSentAt", "LocalBalanceMsat", "LocalCommitmentNumber", "LocalFundingContributionSatoshis", "LocalNextHtlcId", "LocalRevocationNumber", "LocalShutdownScript", "MaxDustHtlcExposureMsat", "RemoteAlias", "RemoteAnnouncementBitcoinSig", "RemoteAnnouncementNodeSig", "RemoteBalanceMsat", "RemoteCommitmentNumber", "RemoteFundingContributionSatoshis", "RemoteNextHtlcId", "RemoteNextPerCommitmentPoint", "RemoteNodeId", "RemoteRevocationNumber", "RemoteShutdownScript", "RevocationLogFromNumber", "SentCommitDiff", "ShortChannelId", "State", "Version")
                    SELECT "ChannelId", "ChangeAddressAddressType", "ChangeAddressIndex", "ChangeAddressIsChange", "ClosingTransaction", "ClosingTxId", "DataLossDetected", "ErrorSent", "FirstRemoteHtlcIdAfterLocalShutdown", "FundingAmountSatoshis", "FundingCreatedAtBlockHeight", "FundingOutputIndex", "FundingTxId", "IsDualFunded", "IsInitiator", "LastReceivedSignature", "LastSentOrder", "LastSentSignature", "LocalAnnouncementSigsSentAt", "LocalBalanceMsat", "LocalCommitmentNumber", "LocalFundingContributionSatoshis", "LocalNextHtlcId", "LocalRevocationNumber", "LocalShutdownScript", "MaxDustHtlcExposureMsat", "RemoteAlias", "RemoteAnnouncementBitcoinSig", "RemoteAnnouncementNodeSig", "RemoteBalanceMsat", "RemoteCommitmentNumber", "RemoteFundingContributionSatoshis", "RemoteNextHtlcId", "RemoteNextPerCommitmentPoint", "RemoteNodeId", "RemoteRevocationNumber", "RemoteShutdownScript", "RevocationLogFromNumber", "SentCommitDiff", "ShortChannelId", "State", "Version"
                    FROM "Channels";
                    DROP TABLE "Channels";
                    ALTER TABLE "ef_temp_Channels" RENAME TO "Channels";
                    CREATE INDEX "IX_Channels_ChangeAddressIndex_ChangeAddressIsChange_ChangeAddressAddressType" ON "Channels" ("ChangeAddressIndex", "ChangeAddressIsChange", "ChangeAddressAddressType");
                    """);
                migrationBuilder.Sql("PRAGMA foreign_keys=1;", suppressTransaction: true);
            }
            else
            {
                migrationBuilder.DropForeignKey(
                    name: "FK_Channels_Peers_PeerEntityNodeId",
                    table: "Channels");

                migrationBuilder.DropColumn(
                    name: "ChangeAddressType",
                    table: "Channels");

                migrationBuilder.DropColumn(
                    name: "PeerEntityNodeId",
                    table: "Channels");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "ChangeAddressType",
                table: "Channels",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "PeerEntityNodeId",
                table: "Channels",
                type: "BLOB",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Channels_PeerEntityNodeId",
                table: "Channels",
                column: "PeerEntityNodeId");

            migrationBuilder.AddForeignKey(
                name: "FK_Channels_Peers_PeerEntityNodeId",
                table: "Channels",
                column: "PeerEntityNodeId",
                principalTable: "Peers",
                principalColumn: "NodeId");
        }
    }
}