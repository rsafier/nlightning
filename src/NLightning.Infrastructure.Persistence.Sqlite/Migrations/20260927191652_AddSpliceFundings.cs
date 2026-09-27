using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
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
                type: "BLOB",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "FundingTxId",
                table: "Commitments",
                type: "BLOB",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<bool>(
                name: "IsDualFunded",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "LocalFundingContributionSatoshis",
                table: "Channels",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RemoteFundingContributionSatoshis",
                table: "Channels",
                type: "INTEGER",
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
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    FundingTxId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    OutputIndex = table.Column<ushort>(type: "INTEGER", nullable: false),
                    CapacitySatoshis = table.Column<long>(type: "INTEGER", nullable: false),
                    LocalFundingPubKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    RemoteFundingPubKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    LocalFundingKeyIndex = table.Column<uint>(type: "INTEGER", nullable: false),
                    LocalBalanceDeltaMsat = table.Column<long>(type: "INTEGER", nullable: false),
                    RemoteBalanceDeltaMsat = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<byte>(type: "INTEGER", nullable: false),
                    Status = table.Column<byte>(type: "INTEGER", nullable: false),
                    FeeratePerKw = table.Column<uint>(type: "INTEGER", nullable: true),
                    Locktime = table.Column<uint>(type: "INTEGER", nullable: true),
                    RbfOf = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ConfirmedHeight = table.Column<uint>(type: "INTEGER", nullable: true),
                    ShortChannelId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    SpliceLockedSent = table.Column<bool>(type: "INTEGER", nullable: false),
                    SpliceLockedReceived = table.Column<bool>(type: "INTEGER", nullable: false),
                    AnnouncementSignaturesReceived = table.Column<bool>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false)
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
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    FeeBaseMsat = table.Column<uint>(type: "INTEGER", nullable: true),
                    FeeProportionalMillionths = table.Column<uint>(type: "INTEGER", nullable: true),
                    CltvExpiryDelta = table.Column<ushort>(type: "INTEGER", nullable: true),
                    HtlcMinimumMsat = table.Column<ulong>(type: "INTEGER", nullable: true),
                    HtlcMaximumMsat = table.Column<ulong>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
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
            migrationBuilder.Sql("UPDATE \"Commitments\" SET \"FundingTxId\" = (SELECT c.\"FundingTxId\" FROM \"Channels\" AS c WHERE c.\"ChannelId\" = \"Commitments\".\"ChannelId\");");
            migrationBuilder.Sql("UPDATE \"RevokedCommitments\" SET \"FundingTxId\" = (SELECT c.\"FundingTxId\" FROM \"Channels\" AS c WHERE c.\"ChannelId\" = \"RevokedCommitments\".\"ChannelId\");");
            migrationBuilder.Sql("INSERT INTO \"ChannelFundings\" (\"ChannelId\", \"FundingTxId\", \"OutputIndex\", \"CapacitySatoshis\", \"LocalFundingPubKey\", \"RemoteFundingPubKey\", \"LocalFundingKeyIndex\", \"LocalBalanceDeltaMsat\", \"RemoteBalanceDeltaMsat\", \"Kind\", \"Status\", \"ShortChannelId\", \"SpliceLockedSent\", \"SpliceLockedReceived\", \"AnnouncementSignaturesReceived\", \"Sequence\") SELECT c.\"ChannelId\", c.\"FundingTxId\", c.\"FundingOutputIndex\", c.\"FundingAmountSatoshis\", l.\"FundingPubKey\", r.\"FundingPubKey\", 0, 0, 0, 0, 1, c.\"ShortChannelId\", 0, 0, 0, 0 FROM \"Channels\" AS c INNER JOIN \"ChannelKeySets\" AS l ON l.\"ChannelId\" = c.\"ChannelId\" AND l.\"IsLocal\" = 1 INNER JOIN \"ChannelKeySets\" AS r ON r.\"ChannelId\" = c.\"ChannelId\" AND r.\"IsLocal\" = 0 WHERE c.\"FundingTxId\" <> zeroblob(32);");
            // ---- End of the hand-written data step ----
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ---- Hand-written guard: refuse while a channel runs on a locked splice. Its rotated funding keys live
            // only in the ChannelFundings row (the key sets keep the initial keys), so a build without that table would
            // rebuild the funding script and sign with the wrong key ----
            migrationBuilder.Sql("DROP TABLE IF EXISTS temp.\"AddSpliceFundingsDownGuard\";");
            migrationBuilder.Sql("CREATE TEMP TABLE \"AddSpliceFundingsDownGuard\" (\"LockedSplices\" INTEGER NOT NULL CONSTRAINT \"AddSpliceFundings_Down_refused_a_channel_runs_on_a_locked_splice_whose_funding_keys_only_ChannelFundings_holds\" CHECK (\"LockedSplices\" = 0));");
            migrationBuilder.Sql("INSERT INTO \"AddSpliceFundingsDownGuard\" (\"LockedSplices\") SELECT COUNT(*) FROM \"ChannelFundings\" WHERE \"Status\" = 1 AND \"Kind\" <> 0;");
            migrationBuilder.Sql("DROP TABLE temp.\"AddSpliceFundingsDownGuard\";");
            // ---- End of the hand-written guard ----

            // ---- Hand-written data step: before the old keys come back, keep only the rows of the current funding ----
            migrationBuilder.Sql("DELETE FROM \"Commitments\" WHERE \"FundingTxId\" <> (SELECT c.\"FundingTxId\" FROM \"Channels\" AS c WHERE c.\"ChannelId\" = \"Commitments\".\"ChannelId\");");
            migrationBuilder.Sql("DELETE FROM \"RevokedCommitments\" WHERE \"FundingTxId\" <> (SELECT c.\"FundingTxId\" FROM \"Channels\" AS c WHERE c.\"ChannelId\" = \"RevokedCommitments\".\"ChannelId\");");
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