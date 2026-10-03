using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddSimpleTaprootChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "PartialSignature",
                table: "Commitments",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "LastReceivedPartialSignature",
                table: "Channels",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RemoteNextNonces",
                table: "Channels",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OptionSimpleTaproot",
                table: "ChannelConfigs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PartialSignature",
                table: "Commitments");

            migrationBuilder.DropColumn(
                name: "LastReceivedPartialSignature",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "RemoteNextNonces",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "OptionSimpleTaproot",
                table: "ChannelConfigs");
        }
    }
}
