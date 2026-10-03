using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelScidAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "remote_alias",
                table: "channels",
                type: "bytea",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "channel_local_aliases",
                columns: table => new
                {
                    alias = table.Column<byte[]>(type: "bytea", nullable: false),
                    channel_id = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_local_aliases", x => x.alias);
                    table.ForeignKey(
                        name: "fk_channel_local_aliases_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "channel_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_channel_local_aliases_channel_id",
                table: "channel_local_aliases",
                column: "channel_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_local_aliases");

            migrationBuilder.DropColumn(
                name: "remote_alias",
                table: "channels");
        }
    }
}