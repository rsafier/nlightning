using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
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
                newName: "FailReason");

            migrationBuilder.RenameColumn(
                name: "AddMessageBytes",
                table: "Htlcs",
                newName: "OnionRoutingPacket");

            migrationBuilder.AddColumn<int>(
                name: "FailureCode",
                table: "Htlcs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "KnownPreimage",
                table: "Htlcs",
                type: "varbinary(32)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "OnionSharedSecret",
                table: "Htlcs",
                type: "varbinary(32)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "PathKey",
                table: "Htlcs",
                type: "varbinary(33)",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "RemovalKind",
                table: "Htlcs",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "Sha256OfOnion",
                table: "Htlcs",
                type: "varbinary(32)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DataLossDetected",
                table: "Channels",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<byte[]>(
                name: "ErrorSent",
                table: "Channels",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "LastSentOrder",
                table: "Channels",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<byte[]>(
                name: "RemoteNextPerCommitmentPoint",
                table: "Channels",
                type: "varbinary(33)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "SentCommitDiff",
                table: "Channels",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Commitments",
                columns: table => new
                {
                    ChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    Slot = table.Column<byte>(type: "tinyint", nullable: false),
                    Number = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    FeeratePerKw = table.Column<long>(type: "bigint", nullable: false),
                    LocalMsat = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    RemoteMsat = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    Htlcs = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    PerCommitmentPoint = table.Column<byte[]>(type: "varbinary(33)", nullable: true),
                    Signature = table.Column<byte[]>(type: "varbinary(64)", nullable: true),
                    HtlcSignatures = table.Column<byte[]>(type: "varbinary(max)", nullable: true)
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
                    ChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    Sequence = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    FeeratePerKw = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<byte>(type: "tinyint", nullable: false)
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
                "UPDATE [Htlcs] SET [OnionRoutingPacket] = CASE WHEN DATALENGTH([OnionRoutingPacket]) >= 1452 THEN SUBSTRING([OnionRoutingPacket], 87, 1366) ELSE 0x END;");

            // The legacy per-HTLC signature column was renamed by the scaffolder; its bytes are not a failure reason (HTLC
            // signatures now live on the commitment rows).
            migrationBuilder.Sql(
                "UPDATE [Htlcs] SET [FailReason] = NULL;");

            // NL-232: the peer's next per-commitment point of channels that already received channel_ready is the remote
            // key set's current point (its index was decremented by channel_ready).
            migrationBuilder.Sql(
                "UPDATE [Channels] SET [RemoteNextPerCommitmentPoint] = (SELECT k.[CurrentPerCommitmentPoint] FROM [ChannelKeySets] k WHERE k.[ChannelId] = [Channels].[ChannelId] AND k.[IsLocal] = 0 AND k.[CurrentPerCommitmentIndex] < 281474976710655);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Commitments");

            migrationBuilder.DropTable(
                name: "FeeUpdates");

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
                name: "Sha256OfOnion",
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
                name: "OnionRoutingPacket",
                table: "Htlcs",
                newName: "AddMessageBytes");

            migrationBuilder.RenameColumn(
                name: "FailReason",
                table: "Htlcs",
                newName: "Signature");

            migrationBuilder.AddColumn<decimal>(
                name: "ObscuredCommitmentNumber",
                table: "Htlcs",
                type: "decimal(20,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<byte[]>(
                name: "LastRevealedPerCommitmentSecret",
                table: "ChannelKeySets",
                type: "varbinary(max)",
                nullable: true);
        }
    }
}