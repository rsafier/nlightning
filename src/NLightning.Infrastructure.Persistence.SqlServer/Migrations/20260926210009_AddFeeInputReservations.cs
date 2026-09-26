using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    FeeSats = table.Column<long>(type: "bigint", nullable: false),
                    ChangeAmountSats = table.Column<long>(type: "bigint", nullable: false),
                    ChangeScript = table.Column<byte[]>(type: "varbinary(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeeInputReservations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FeeInputReservationInputs",
                columns: table => new
                {
                    TransactionId = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    Index = table.Column<long>(type: "bigint", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AmountSats = table.Column<long>(type: "bigint", nullable: false),
                    AddressType = table.Column<byte>(type: "tinyint", nullable: false),
                    ScriptPubKey = table.Column<byte[]>(type: "varbinary(64)", maxLength: 64, nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false)
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