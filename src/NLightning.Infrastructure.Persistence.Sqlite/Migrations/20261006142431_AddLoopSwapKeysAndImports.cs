using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
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
                    Script = table.Column<byte[]>(type: "BLOB", maxLength: 34, nullable: false),
                    InternalKey = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: false),
                    Definition = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedHeight = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedTapscripts", x => x.Script);
                });

            migrationBuilder.CreateTable(
                name: "KeyRingKeys",
                columns: table => new
                {
                    Family = table.Column<int>(type: "INTEGER", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    PublicKey = table.Column<byte[]>(type: "BLOB", maxLength: 33, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
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