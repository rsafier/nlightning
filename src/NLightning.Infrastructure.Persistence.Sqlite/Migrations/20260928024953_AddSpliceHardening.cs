using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
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
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<byte[]>(
                name: "SignedOnFundings",
                table: "Commitments",
                type: "BLOB",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ---- Hand-written guard: refuse while a remote commitment records the fundings it was signed on (NL-494).
            // That record is the only proof that a discarded or replaced funding carries the commitment too, so a build
            // without the column would not log its revocation there (SP-I5). One commitment round after the splice
            // settles clears it ----
            migrationBuilder.Sql("DROP TABLE IF EXISTS temp.\"AddSpliceHardeningDownGuard\";");
            migrationBuilder.Sql("CREATE TEMP TABLE \"AddSpliceHardeningDownGuard\" (\"SignedOnFundings\" INTEGER NOT NULL CONSTRAINT \"AddSpliceHardening_Down_refused_a_remote_commitment_records_the_fundings_it_was_signed_on\" CHECK (\"SignedOnFundings\" = 0));");
            migrationBuilder.Sql("INSERT INTO \"AddSpliceHardeningDownGuard\" (\"SignedOnFundings\") SELECT COUNT(*) FROM \"Commitments\" WHERE \"SignedOnFundings\" IS NOT NULL;");
            migrationBuilder.Sql("DROP TABLE temp.\"AddSpliceHardeningDownGuard\";");
            // ---- End of the hand-written guard ----

            // ---- Hand-written data step: inbound-only peers (NL-497) have no address; a build without the column would
            // dial their empty address, and had no row for them before this migration ----
            migrationBuilder.Sql("DELETE FROM \"Peers\" WHERE \"IsInboundOnly\" = 1;");
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