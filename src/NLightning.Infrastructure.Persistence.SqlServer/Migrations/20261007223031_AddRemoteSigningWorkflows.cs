using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
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
                    WorkflowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    ActiveChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    ExpectedLocalCommitmentNumber = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    ExpectedRemoteCommitmentNumber = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    SnapshotFingerprint = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    SignerIdentity = table.Column<byte[]>(type: "varbinary(33)", maxLength: 33, nullable: false),
                    Network = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    CreatedAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtTicks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigningWorkflows", x => x.WorkflowId);
                });

            migrationBuilder.CreateTable(
                name: "SigningRequests",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Operation = table.Column<long>(type: "bigint", nullable: false),
                    Envelope = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    ArgumentFingerprint = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    Response = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    CreatedAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtTicks = table.Column<long>(type: "bigint", nullable: false)
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
                unique: true,
                filter: "[ActiveChannelId] IS NOT NULL");

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