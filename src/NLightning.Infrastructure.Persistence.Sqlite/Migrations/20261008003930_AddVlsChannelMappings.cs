using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddVlsChannelMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VlsChannelMappings",
                columns: table => new
                {
                    KeyIndex = table.Column<uint>(type: "INTEGER", nullable: false),
                    DbId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    PeerId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    SignerIdentity = table.Column<byte[]>(type: "BLOB", maxLength: 33, nullable: false),
                    Network = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AllocationRequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AllocationEnvelope = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AllocationResponse = table.Column<byte[]>(type: "BLOB", nullable: true),
                    VlsChannelId = table.Column<byte[]>(type: "BLOB", maxLength: 41, nullable: true),
                    CreatedAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VlsChannelMappings", x => x.KeyIndex);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VlsChannelMappings_AllocationRequestId",
                table: "VlsChannelMappings",
                column: "AllocationRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VlsChannelMappings_ChannelId",
                table: "VlsChannelMappings",
                column: "ChannelId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VlsChannelMappings_DbId",
                table: "VlsChannelMappings",
                column: "DbId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VlsChannelMappings");
        }
    }
}