using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NLightning.Infrastructure.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddGossipGraph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "local_announcement_sigs_sent_at",
                table: "channels",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "remote_announcement_bitcoin_sig",
                table: "channels",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "remote_announcement_node_sig",
                table: "channels",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "announce_channel",
                table: "channel_configs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "graph_banned_nodes",
                columns: table => new
                {
                    node_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    until = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_graph_banned_nodes", x => x.node_id);
                });

            migrationBuilder.CreateTable(
                name: "graph_channels",
                columns: table => new
                {
                    short_channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    node_id1 = table.Column<byte[]>(type: "bytea", nullable: false),
                    node_id2 = table.Column<byte[]>(type: "bytea", nullable: false),
                    bitcoin_key1 = table.Column<byte[]>(type: "bytea", nullable: false),
                    bitcoin_key2 = table.Column<byte[]>(type: "bytea", nullable: false),
                    capacity_sat = table.Column<long>(type: "bigint", nullable: false),
                    features = table.Column<byte[]>(type: "bytea", nullable: false),
                    raw_announcement = table.Column<byte[]>(type: "bytea", nullable: false),
                    verification = table.Column<byte>(type: "smallint", nullable: false),
                    spent_at_height = table.Column<long>(type: "bigint", nullable: true),
                    received_at = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_graph_channels", x => x.short_channel_id);
                });

            migrationBuilder.CreateTable(
                name: "graph_nodes",
                columns: table => new
                {
                    node_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    timestamp = table.Column<long>(type: "bigint", nullable: false),
                    features = table.Column<byte[]>(type: "bytea", nullable: false),
                    alias = table.Column<byte[]>(type: "bytea", nullable: false),
                    color = table.Column<byte[]>(type: "bytea", nullable: false),
                    addresses = table.Column<byte[]>(type: "bytea", nullable: false),
                    raw_announcement = table.Column<byte[]>(type: "bytea", nullable: false),
                    received_at = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_graph_nodes", x => x.node_id);
                });

            migrationBuilder.CreateTable(
                name: "graph_channel_policies",
                columns: table => new
                {
                    short_channel_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    direction = table.Column<byte>(type: "smallint", nullable: false),
                    timestamp = table.Column<long>(type: "bigint", nullable: false),
                    message_flags = table.Column<byte>(type: "smallint", nullable: false),
                    channel_flags = table.Column<byte>(type: "smallint", nullable: false),
                    cltv_expiry_delta = table.Column<int>(type: "integer", nullable: false),
                    htlc_minimum_msat = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    htlc_maximum_msat = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    fee_base_msat = table.Column<long>(type: "bigint", nullable: false),
                    fee_ppm = table.Column<long>(type: "bigint", nullable: false),
                    raw_update = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_graph_channel_policies", x => new { x.short_channel_id, x.direction });
                    table.ForeignKey(
                        name: "fk_graph_channel_policies_graph_channels_short_channel_id",
                        column: x => x.short_channel_id,
                        principalTable: "graph_channels",
                        principalColumn: "short_channel_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_graph_channels_node_id1",
                table: "graph_channels",
                column: "node_id1");

            migrationBuilder.CreateIndex(
                name: "ix_graph_channels_node_id2",
                table: "graph_channels",
                column: "node_id2");

            migrationBuilder.CreateIndex(
                name: "ix_graph_channels_spent_at_height",
                table: "graph_channels",
                column: "spent_at_height");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "graph_banned_nodes");

            migrationBuilder.DropTable(
                name: "graph_channel_policies");

            migrationBuilder.DropTable(
                name: "graph_nodes");

            migrationBuilder.DropTable(
                name: "graph_channels");

            migrationBuilder.DropColumn(
                name: "local_announcement_sigs_sent_at",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "remote_announcement_bitcoin_sig",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "remote_announcement_node_sig",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "announce_channel",
                table: "channel_configs");
        }
    }
}