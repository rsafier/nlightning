using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddLoopSwapKeysAndImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportedTapscripts",
                columns: table => new
                {
                    Script = table.Column<byte[]>(type: "varbinary(34)", maxLength: 34, nullable: false),
                    InternalKey = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    Definition = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CreatedHeight = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedTapscripts", x => x.Script);
                });

            migrationBuilder.CreateTable(
                name: "KeyRingKeys",
                columns: table => new
                {
                    Family = table.Column<int>(type: "int", nullable: false),
                    Index = table.Column<int>(type: "int", nullable: false),
                    PublicKey = table.Column<byte[]>(type: "varbinary(33)", maxLength: 33, nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeyRingKeys", x => new { x.Family, x.Index });
                });

            migrationBuilder.CreateIndex(
                name: "IX_KeyRingKeys_PublicKey",
                table: "KeyRingKeys",
                column: "PublicKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportedTapscripts");

            migrationBuilder.DropTable(
                name: "KeyRingKeys");
        }
    }
}