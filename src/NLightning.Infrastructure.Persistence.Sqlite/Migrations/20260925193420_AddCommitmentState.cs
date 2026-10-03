using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddCommitmentState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ObscuredCommitmentNumber",
                table: "Htlcs");

            migrationBuilder.DropColumn(
                name: "LastRevealedPerCommitmentSecret",
                table: "ChannelKeySets");

            migrationBuilder.RenameColumn(
                name: "Signature",
                table: "Htlcs",
                newName: "Sha256OfOnion");

            migrationBuilder.RenameColumn(
                name: "AddMessageBytes",
                table: "Htlcs",
                newName: "OnionRoutingPacket");

            migrationBuilder.AddColumn<byte[]>(
                name: "FailReason",
                table: "Htlcs",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<ushort>(
                name: "FailureCode",
                table: "Htlcs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "KnownPreimage",
                table: "Htlcs",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "OnionSharedSecret",
                table: "Htlcs",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "PathKey",
                table: "Htlcs",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "RemovalKind",
                table: "Htlcs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DataLossDetected",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<byte[]>(
                name: "ErrorSent",
                table: "Channels",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "LastSentOrder",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<byte[]>(
                name: "RemoteNextPerCommitmentPoint",
                table: "Channels",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "SentCommitDiff",
                table: "Channels",
                type: "BLOB",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Commitments",
                columns: table => new
                {
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Slot = table.Column<byte>(type: "INTEGER", nullable: false),
                    Number = table.Column<ulong>(type: "INTEGER", nullable: false),
                    FeeratePerKw = table.Column<uint>(type: "INTEGER", nullable: false),
                    LocalMsat = table.Column<ulong>(type: "INTEGER", nullable: false),
                    RemoteMsat = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Htlcs = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PerCommitmentPoint = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Signature = table.Column<byte[]>(type: "BLOB", nullable: true),
                    HtlcSignatures = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Commitments", x => new { x.ChannelId, x.Slot });
                    table.ForeignKey(
                        name: "FK_Commitments_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "ChannelId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FeeUpdates",
                columns: table => new
                {
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Sequence = table.Column<ulong>(type: "INTEGER", nullable: false),
                    FeeratePerKw = table.Column<uint>(type: "INTEGER", nullable: false),
                    State = table.Column<byte>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeeUpdates", x => new { x.ChannelId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_FeeUpdates_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "ChannelId",
                        onDelete: ReferentialAction.Cascade);
                });

            // ---- Hand-written data steps (not generated by EF) ----
            // NL-025: rows written before the state machine held the whole serialized update_add_htlc (type 2 + channel_id
            // 32 + id 8 + amount 8 + hash 32 + cltv 4 = 86 bytes, then the 1366-byte onion); keep only the onion (empty
            // when the row predates the mandatory onion). Such rows keep their legacy state (0-3) and the node refuses to
            // restore the channel.
            migrationBuilder.Sql(
                "UPDATE \"Htlcs\" SET \"OnionRoutingPacket\" = CASE WHEN length(\"OnionRoutingPacket\") >= 1452 THEN substr(\"OnionRoutingPacket\", 87, 1366) ELSE X'' END;");

            // The legacy per-HTLC signature column was renamed by the scaffolder; its bytes are not an onion hash (HTLC
            // signatures now live on the commitment rows).
            migrationBuilder.Sql(
                "UPDATE \"Htlcs\" SET \"Sha256OfOnion\" = NULL;");

            // NL-232: the peer's next per-commitment point of channels that already received channel_ready is the remote
            // key set's current point (its index was decremented by channel_ready).
            migrationBuilder.Sql(
                "UPDATE \"Channels\" SET \"RemoteNextPerCommitmentPoint\" = (SELECT k.\"CurrentPerCommitmentPoint\" FROM \"ChannelKeySets\" k WHERE k.\"ChannelId\" = \"Channels\".\"ChannelId\" AND k.\"IsLocal\" = 0 AND k.\"CurrentPerCommitmentIndex\" < 281474976710655);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Commitments");

            migrationBuilder.DropTable(
                name: "FeeUpdates");

            migrationBuilder.DropColumn(
                name: "FailReason",
                table: "Htlcs");

            migrationBuilder.DropColumn(
                name: "FailureCode",
                table: "Htlcs");

            migrationBuilder.DropColumn(
                name: "KnownPreimage",
                table: "Htlcs");

            migrationBuilder.DropColumn(
                name: "OnionSharedSecret",
                table: "Htlcs");

            migrationBuilder.DropColumn(
                name: "PathKey",
                table: "Htlcs");

            migrationBuilder.DropColumn(
                name: "RemovalKind",
                table: "Htlcs");

            migrationBuilder.DropColumn(
                name: "DataLossDetected",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "ErrorSent",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "LastSentOrder",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "RemoteNextPerCommitmentPoint",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "SentCommitDiff",
                table: "Channels");

            migrationBuilder.RenameColumn(
                name: "Sha256OfOnion",
                table: "Htlcs",
                newName: "Signature");

            migrationBuilder.RenameColumn(
                name: "OnionRoutingPacket",
                table: "Htlcs",
                newName: "AddMessageBytes");

            migrationBuilder.AddColumn<ulong>(
                name: "ObscuredCommitmentNumber",
                table: "Htlcs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0ul);

            migrationBuilder.AddColumn<byte[]>(
                name: "LastRevealedPerCommitmentSecret",
                table: "ChannelKeySets",
                type: "BLOB",
                nullable: true);
        }
    }
}