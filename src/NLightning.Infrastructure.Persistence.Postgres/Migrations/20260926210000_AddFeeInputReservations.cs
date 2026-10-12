using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFeeInputReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fee_input_reservations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    fee_sats = table.Column<long>(type: "bigint", nullable: false),
                    change_amount_sats = table.Column<long>(type: "bigint", nullable: false),
                    change_script = table.Column<byte[]>(type: "bytea", maxLength: 64, nullable: true),
                    created_at = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fee_input_reservations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "fee_input_reservation_inputs",
                columns: table => new
                {
                    transaction_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    index = table.Column<long>(type: "bigint", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_sats = table.Column<long>(type: "bigint", nullable: false),
                    address_type = table.Column<byte>(type: "smallint", nullable: false),
                    script_pub_key = table.Column<byte[]>(type: "bytea", maxLength: 64, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fee_input_reservation_inputs", x => new { x.transaction_id, x.index });
                    table.ForeignKey(
                        name: "fk_fee_input_reservation_inputs_fee_input_reservations_reserva",
                        column: x => x.reservation_id,
                        principalTable: "fee_input_reservations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_fee_input_reservation_inputs_reservation_id",
                table: "fee_input_reservation_inputs",
                column: "reservation_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fee_input_reservation_inputs");

            migrationBuilder.DropTable(
                name: "fee_input_reservations");
        }
    }
}