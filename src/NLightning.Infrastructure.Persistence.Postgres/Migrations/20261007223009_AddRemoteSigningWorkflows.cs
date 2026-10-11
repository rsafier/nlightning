using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteSigningWorkflows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "signing_workflows",
                columns: table => new
                {
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    active_channel_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    expected_local_commitment_number = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    expected_remote_commitment_number = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    snapshot_fingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    signer_identity = table.Column<byte[]>(type: "bytea", maxLength: 33, nullable: false),
                    network = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    created_at_ticks = table.Column<long>(type: "bigint", nullable: false),
                    updated_at_ticks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signing_workflows", x => x.workflow_id);
                });

            migrationBuilder.CreateTable(
                name: "signing_requests",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    operation = table.Column<long>(type: "bigint", nullable: false),
                    envelope = table.Column<byte[]>(type: "bytea", nullable: false),
                    argument_fingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    response = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at_ticks = table.Column<long>(type: "bigint", nullable: false),
                    updated_at_ticks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signing_requests", x => x.request_id);
                    table.ForeignKey(
                        name: "fk_signing_requests_signing_workflows_workflow_id",
                        column: x => x.workflow_id,
                        principalTable: "signing_workflows",
                        principalColumn: "workflow_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_signing_requests_workflow_id_ordinal",
                table: "signing_requests",
                columns: new[] { "workflow_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_signing_workflows_active_channel_id",
                table: "signing_workflows",
                column: "active_channel_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_signing_workflows_channel_id_state",
                table: "signing_workflows",
                columns: new[] { "channel_id", "state" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "signing_requests");

            migrationBuilder.DropTable(
                name: "signing_workflows");
        }
    }
}