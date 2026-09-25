using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class FixRemoteNodeIdLength : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<byte[]>(
                name: "RemoteNodeId",
                table: "Channels",
                type: "varbinary(33)",
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "varbinary(32)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<byte[]>(
                name: "RemoteNodeId",
                table: "Channels",
                type: "varbinary(32)",
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "varbinary(33)");
        }
    }
}