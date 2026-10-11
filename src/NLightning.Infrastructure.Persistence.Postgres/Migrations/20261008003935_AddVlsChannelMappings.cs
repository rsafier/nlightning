using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddVlsChannelMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vls_channel_mappings",
                columns: table => new
                {
                    key_index = table.Column<long>(type: "bigint", nullable: false),
                    db_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    peer_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: true),
                    signer_identity = table.Column<byte[]>(type: "bytea", maxLength: 33, nullable: false),
                    network = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    allocation_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    allocation_envelope = table.Column<byte[]>(type: "bytea", nullable: false),
                    allocation_response = table.Column<byte[]>(type: "bytea", nullable: true),
                    vls_channel_id = table.Column<byte[]>(type: "bytea", maxLength: 41, nullable: true),
                    created_at_ticks = table.Column<long>(type: "bigint", nullable: false),
                    updated_at_ticks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vls_channel_mappings", x => x.key_index);
                });

            migrationBuilder.CreateIndex(
                name: "ix_vls_channel_mappings_allocation_request_id",
                table: "vls_channel_mappings",
                column: "allocation_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vls_channel_mappings_channel_id",
                table: "vls_channel_mappings",
                column: "channel_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vls_channel_mappings_db_id",
                table: "vls_channel_mappings",
                column: "db_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vls_channel_mappings");
        }
    }
}