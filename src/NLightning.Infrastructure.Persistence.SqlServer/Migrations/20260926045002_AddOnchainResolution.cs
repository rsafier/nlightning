using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddOnchainResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "RevocationLogFromNumber",
                table: "Channels",
                type: "decimal(20,0)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChannelCloses",
                columns: table => new
                {
                    ChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    CommitmentTxId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    CommitmentNumber = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    SpentAtHeight = table.Column<long>(type: "bigint", nullable: false),
                    BlockHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
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
                    TransactionId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    OutputIndex = table.Column<long>(type: "bigint", nullable: false),
                    ChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    Descriptor = table.Column<byte>(type: "tinyint", nullable: false),
                    DescriptorData = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    HtlcDirection = table.Column<byte>(type: "tinyint", nullable: true),
                    HtlcId = table.Column<decimal>(type: "decimal(20,0)", nullable: true),
                    State = table.Column<byte>(type: "tinyint", nullable: false),
                    ResolvingTxId = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    WaitUntilHeight = table.Column<long>(type: "bigint", nullable: true),
                    DeadlineHeight = table.Column<long>(type: "bigint", nullable: true),
                    ResolvedHeight = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
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
                    ChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    Number = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    FeeratePerKw = table.Column<long>(type: "bigint", nullable: false),
                    LocalMsat = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    RemoteMsat = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    Htlcs = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
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
                "UPDATE [Channels] SET [RevocationLogFromNumber] = [RemoteCommitmentNumber] WHERE [RemoteCommitmentNumber] > 0;");
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