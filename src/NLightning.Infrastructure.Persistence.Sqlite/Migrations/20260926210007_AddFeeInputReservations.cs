using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddFeeInputReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeeInputReservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    FeeSats = table.Column<long>(type: "INTEGER", nullable: false),
                    ChangeAmountSats = table.Column<long>(type: "INTEGER", nullable: false),
                    ChangeScript = table.Column<byte[]>(type: "BLOB", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeeInputReservations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FeeInputReservationInputs",
                columns: table => new
                {
                    TransactionId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Index = table.Column<uint>(type: "INTEGER", nullable: false),
                    ReservationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountSats = table.Column<long>(type: "INTEGER", nullable: false),
                    AddressType = table.Column<byte>(type: "INTEGER", nullable: false),
                    ScriptPubKey = table.Column<byte[]>(type: "BLOB", maxLength: 64, nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeeInputReservationInputs", x => new { x.TransactionId, x.Index });
                    table.ForeignKey(
                        name: "FK_FeeInputReservationInputs_FeeInputReservations_ReservationId",
                        column: x => x.ReservationId,
                        principalTable: "FeeInputReservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeeInputReservationInputs_ReservationId",
                table: "FeeInputReservationInputs",
                column: "ReservationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeeInputReservationInputs");

            migrationBuilder.DropTable(
                name: "FeeInputReservations");
        }
    }
}