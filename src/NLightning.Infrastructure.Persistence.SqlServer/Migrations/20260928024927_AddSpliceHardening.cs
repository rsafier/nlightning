using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddSpliceHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsInboundOnly",
                table: "Peers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<byte[]>(
                name: "SignedOnFundings",
                table: "Commitments",
                type: "varbinary(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ---- Hand-written guard: refuse while a remote commitment records the fundings it was signed on (NL-494).
            // That record is the only proof that a discarded or replaced funding carries the commitment too, so a build
            // without the column would not log its revocation there (SP-I5). One commitment round after the splice
            // settles clears it ----
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [Commitments] WHERE [SignedOnFundings] IS NOT NULL) THROW 50000, N'AddSpliceHardening Down refused: a remote commitment records the fundings it was signed on', 1;");
            // ---- End of the hand-written guard ----

            // ---- Hand-written data step: inbound-only peers (NL-497) have no address; a build without the column would
            // dial their empty address, and had no row for them before this migration ----
            migrationBuilder.Sql("DELETE FROM [Peers] WHERE [IsInboundOnly] = 1;");
            // ---- End of the hand-written data step ----

            migrationBuilder.DropColumn(
                name: "IsInboundOnly",
                table: "Peers");

            migrationBuilder.DropColumn(
                name: "SignedOnFundings",
                table: "Commitments");
        }
    }
}