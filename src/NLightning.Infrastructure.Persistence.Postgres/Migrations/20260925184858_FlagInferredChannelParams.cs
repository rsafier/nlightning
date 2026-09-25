using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class FlagInferredChannelParams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "has_inferred_params",
                table: "channel_configs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Data step: every row that exists now was written before SplitChannelParams (both migrations ship
            // together), so its per-side parameters are partly inferred (see ChannelParams.HasInferredParams)
            migrationBuilder.Sql(
                "UPDATE channel_configs SET has_inferred_params = TRUE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "has_inferred_params",
                table: "channel_configs");
        }
    }
}