using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSpliceHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_inbound_only",
                table: "peers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<byte[]>(
                name: "signed_on_fundings",
                table: "commitments",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ---- Hand-written guard: refuse while a remote commitment records the fundings it was signed on (NL-494).
            // That record is the only proof that a discarded or replaced funding carries the commitment too, so a build
            // without the column would not log its revocation there (SP-I5). One commitment round after the splice
            // settles clears it ----
            migrationBuilder.Sql("DO $$ BEGIN IF EXISTS (SELECT 1 FROM commitments WHERE signed_on_fundings IS NOT NULL) THEN RAISE EXCEPTION 'AddSpliceHardening Down refused: a remote commitment records the fundings it was signed on'; END IF; END $$;");
            // ---- End of the hand-written guard ----

            // ---- Hand-written data step: inbound-only peers (NL-497) have no address; a build without the column would
            // dial their empty address, and had no row for them before this migration ----
            migrationBuilder.Sql("DELETE FROM peers WHERE is_inbound_only;");
            // ---- End of the hand-written data step ----

            migrationBuilder.DropColumn(
                name: "is_inbound_only",
                table: "peers");

            migrationBuilder.DropColumn(
                name: "signed_on_fundings",
                table: "commitments");
        }
    }
}