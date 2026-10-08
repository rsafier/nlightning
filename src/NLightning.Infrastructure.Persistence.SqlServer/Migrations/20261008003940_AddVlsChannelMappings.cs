using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
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
                    KeyIndex = table.Column<long>(type: "bigint", nullable: false),
                    DbId = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    PeerId = table.Column<byte[]>(type: "varbinary(33)", nullable: false),
                    ChannelId = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    SignerIdentity = table.Column<byte[]>(type: "varbinary(33)", maxLength: 33, nullable: false),
                    Network = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AllocationRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AllocationEnvelope = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    AllocationResponse = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    VlsChannelId = table.Column<byte[]>(type: "varbinary(41)", maxLength: 41, nullable: true),
                    CreatedAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtTicks = table.Column<long>(type: "bigint", nullable: false)
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
                unique: true,
                filter: "[ChannelId] IS NOT NULL");

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