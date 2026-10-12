using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class FlagInferredChannelParams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasInferredParams",
                table: "ChannelConfigs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Data step: every row that exists now was written before SplitChannelParams (both migrations ship
            // together), so its per-side parameters are partly inferred (see ChannelParams.HasInferredParams)
            migrationBuilder.Sql(
                "UPDATE [ChannelConfigs] SET [HasInferredParams] = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasInferredParams",
                table: "ChannelConfigs");
        }
    }
}