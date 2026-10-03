using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
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
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Purpose = table.Column<byte>(type: "INTEGER", nullable: false),
                    IsInitiator = table.Column<bool>(type: "INTEGER", nullable: false),
                    FeeratePerKw = table.Column<uint>(type: "INTEGER", nullable: false),
                    Locktime = table.Column<uint>(type: "INTEGER", nullable: false),
                    Inputs = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Outputs = table.Column<byte[]>(type: "BLOB", nullable: false),
                    LocalContribution = table.Column<byte[]>(type: "BLOB", nullable: false),
                    LocalReservationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ConstructedTx = table.Column<byte[]>(type: "BLOB", nullable: true),
                    OurWitnesses = table.Column<byte[]>(type: "BLOB", nullable: true),
                    TheirWitnesses = table.Column<byte[]>(type: "BLOB", nullable: true),
                    OurSharedInputSignature = table.Column<byte[]>(type: "BLOB", nullable: true),
                    TheirSharedInputSignature = table.Column<byte[]>(type: "BLOB", nullable: true),
                    CommitmentSignedSent = table.Column<bool>(type: "INTEGER", nullable: false),
                    CommitmentSignedReceived = table.Column<bool>(type: "INTEGER", nullable: false),
                    TxSignaturesSent = table.Column<bool>(type: "INTEGER", nullable: false),
                    TxSignaturesReceived = table.Column<bool>(type: "INTEGER", nullable: false),
                    State = table.Column<byte>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ResolvedAt = table.Column<long>(type: "INTEGER", nullable: true)
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