using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteSigningWorkflows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SigningWorkflows",
                columns: table => new
                {
                    WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ActiveChannelId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpectedLocalCommitmentNumber = table.Column<ulong>(type: "INTEGER", nullable: false),
                    ExpectedRemoteCommitmentNumber = table.Column<ulong>(type: "INTEGER", nullable: false),
                    SnapshotFingerprint = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: false),
                    SignerIdentity = table.Column<byte[]>(type: "BLOB", maxLength: 33, nullable: false),
                    Network = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigningWorkflows", x => x.WorkflowId);
                });

            migrationBuilder.CreateTable(
                name: "SigningRequests",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    Operation = table.Column<uint>(type: "INTEGER", nullable: false),
                    Envelope = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ArgumentFingerprint = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Response = table.Column<byte[]>(type: "BLOB", nullable: true),
                    CreatedAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigningRequests", x => x.RequestId);
                    table.ForeignKey(
                        name: "FK_SigningRequests_SigningWorkflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "SigningWorkflows",
                        principalColumn: "WorkflowId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SigningRequests_WorkflowId_Ordinal",
                table: "SigningRequests",
                columns: new[] { "WorkflowId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SigningWorkflows_ActiveChannelId",
                table: "SigningWorkflows",
                column: "ActiveChannelId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SigningWorkflows_ChannelId_State",
                table: "SigningWorkflows",
                columns: new[] { "ChannelId", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SigningRequests");

            migrationBuilder.DropTable(
                name: "SigningWorkflows");
        }
    }
}