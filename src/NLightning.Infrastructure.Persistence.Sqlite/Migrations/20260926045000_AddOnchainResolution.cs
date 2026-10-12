using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddOnchainResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<ulong>(
                name: "RevocationLogFromNumber",
                table: "Channels",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChannelCloses",
                columns: table => new
                {
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Kind = table.Column<byte>(type: "INTEGER", nullable: false),
                    CommitmentTxId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CommitmentNumber = table.Column<ulong>(type: "INTEGER", nullable: true),
                    SpentAtHeight = table.Column<uint>(type: "INTEGER", nullable: false),
                    BlockHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelCloses", x => x.ChannelId);
                    table.ForeignKey(
                        name: "FK_ChannelCloses_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "ChannelId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OutputResolutions",
                columns: table => new
                {
                    TransactionId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    OutputIndex = table.Column<uint>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Descriptor = table.Column<byte>(type: "INTEGER", nullable: false),
                    DescriptorData = table.Column<byte[]>(type: "BLOB", nullable: false),
                    HtlcDirection = table.Column<byte>(type: "INTEGER", nullable: true),
                    HtlcId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    State = table.Column<byte>(type: "INTEGER", nullable: false),
                    ResolvingTxId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    WaitUntilHeight = table.Column<uint>(type: "INTEGER", nullable: true),
                    DeadlineHeight = table.Column<uint>(type: "INTEGER", nullable: true),
                    ResolvedHeight = table.Column<uint>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutputResolutions", x => new { x.TransactionId, x.OutputIndex });
                    table.ForeignKey(
                        name: "FK_OutputResolutions_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "ChannelId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RevokedCommitments",
                columns: table => new
                {
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Number = table.Column<ulong>(type: "INTEGER", nullable: false),
                    FeeratePerKw = table.Column<uint>(type: "INTEGER", nullable: false),
                    LocalMsat = table.Column<ulong>(type: "INTEGER", nullable: false),
                    RemoteMsat = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Htlcs = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RevokedCommitments", x => new { x.ChannelId, x.Number });
                    table.ForeignKey(
                        name: "FK_RevokedCommitments_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "ChannelId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutputResolutions_ChannelId",
                table: "OutputResolutions",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_OutputResolutions_State",
                table: "OutputResolutions",
                column: "State");

            // ---- Hand-written data step (not generated) ----
            // BOLT 5 plan O1-T3 (§8 risk 5): the revocation log starts now. A channel whose peer already revoked
            // commitments (RemoteCommitmentNumber > 0) records the first number the log covers, so a breach at an older
            // number is reported as "HTLC outputs unrecoverable" instead of being under-penalized. Newer channels keep
            // null (the log covers every number).
            migrationBuilder.Sql(
                "UPDATE \"Channels\" SET \"RevocationLogFromNumber\" = \"RemoteCommitmentNumber\" WHERE \"RemoteCommitmentNumber\" > 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelCloses");

            migrationBuilder.DropTable(
                name: "OutputResolutions");

            migrationBuilder.DropTable(
                name: "RevokedCommitments");

            migrationBuilder.DropColumn(
                name: "RevocationLogFromNumber",
                table: "Channels");
        }
    }
}