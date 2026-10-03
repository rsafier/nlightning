using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddInteractiveTxSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InteractiveTxSessions",
                columns: table => new
                {
                    ChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Purpose = table.Column<byte>(type: "tinyint", nullable: false),
                    IsInitiator = table.Column<bool>(type: "bit", nullable: false),
                    FeeratePerKw = table.Column<long>(type: "bigint", nullable: false),
                    Locktime = table.Column<long>(type: "bigint", nullable: false),
                    Inputs = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Outputs = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    LocalContribution = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    LocalReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConstructedTx = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    OurWitnesses = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    TheirWitnesses = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    OurSharedInputSignature = table.Column<byte[]>(type: "varbinary(64)", nullable: true),
                    TheirSharedInputSignature = table.Column<byte[]>(type: "varbinary(64)", nullable: true),
                    CommitmentSignedSent = table.Column<bool>(type: "bit", nullable: false),
                    CommitmentSignedReceived = table.Column<bool>(type: "bit", nullable: false),
                    TxSignaturesSent = table.Column<bool>(type: "bit", nullable: false),
                    TxSignaturesReceived = table.Column<bool>(type: "bit", nullable: false),
                    State = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    ResolvedAt = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InteractiveTxSessions", x => new { x.ChannelId, x.SessionId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_InteractiveTxSessions_ResolvedAt",
                table: "InteractiveTxSessions",
                column: "ResolvedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InteractiveTxSessions");
        }
    }
}