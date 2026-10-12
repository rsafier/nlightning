using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
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
                    script = table.Column<byte[]>(type: "bytea", maxLength: 34, nullable: false),
                    internal_key = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    definition = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_height = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_imported_tapscripts", x => x.script);
                });

            migrationBuilder.CreateTable(
                name: "KeyRingKeys",
                columns: table => new
                {
                    family = table.Column<int>(type: "integer", nullable: false),
                    index = table.Column<int>(type: "integer", nullable: false),
                    public_key = table.Column<byte[]>(type: "bytea", maxLength: 33, nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_key_ring_keys", x => new { x.family, x.index });
                });

            migrationBuilder.CreateIndex(
                name: "ix_key_ring_keys_public_key",
                table: "KeyRingKeys",
                column: "public_key",
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