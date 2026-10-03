using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddOnchainResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "revocation_log_from_number",
                table: "channels",
                type: "numeric(20,0)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "channel_closes",
                columns: table => new
                {
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    kind = table.Column<byte>(type: "smallint", nullable: false),
                    commitment_tx_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    commitment_number = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    spent_at_height = table.Column<long>(type: "bigint", nullable: false),
                    block_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_closes", x => x.channel_id);
                    table.ForeignKey(
                        name: "fk_channel_closes_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "channel_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "output_resolutions",
                columns: table => new
                {
                    transaction_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    output_index = table.Column<long>(type: "bigint", nullable: false),
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    descriptor = table.Column<byte>(type: "smallint", nullable: false),
                    descriptor_data = table.Column<byte[]>(type: "bytea", nullable: false),
                    htlc_direction = table.Column<byte>(type: "smallint", nullable: true),
                    htlc_id = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    state = table.Column<byte>(type: "smallint", nullable: false),
                    resolving_tx_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    wait_until_height = table.Column<long>(type: "bigint", nullable: true),
                    deadline_height = table.Column<long>(type: "bigint", nullable: true),
                    resolved_height = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_output_resolutions", x => new { x.transaction_id, x.output_index });
                    table.ForeignKey(
                        name: "fk_output_resolutions_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "channel_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "revoked_commitments",
                columns: table => new
                {
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    number = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    feerate_per_kw = table.Column<long>(type: "bigint", nullable: false),
                    local_msat = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    remote_msat = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    htlcs = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_revoked_commitments", x => new { x.channel_id, x.number });
                    table.ForeignKey(
                        name: "fk_revoked_commitments_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "channel_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_output_resolutions_channel_id",
                table: "output_resolutions",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_output_resolutions_state",
                table: "output_resolutions",
                column: "state");

            // ---- Hand-written data step (not generated) ----
            // BOLT 5 plan O1-T3 (§8 risk 5): the revocation log starts now. A channel whose peer already revoked
            // commitments (RemoteCommitmentNumber > 0) records the first number the log covers, so a breach at an older
            // number is reported as "HTLC outputs unrecoverable" instead of being under-penalized. Newer channels keep
            // null (the log covers every number).
            migrationBuilder.Sql(
                "UPDATE channels SET revocation_log_from_number = remote_commitment_number WHERE remote_commitment_number > 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_closes");

            migrationBuilder.DropTable(
                name: "output_resolutions");

            migrationBuilder.DropTable(
                name: "revoked_commitments");

            migrationBuilder.DropColumn(
                name: "revocation_log_from_number",
                table: "channels");
        }
    }
}