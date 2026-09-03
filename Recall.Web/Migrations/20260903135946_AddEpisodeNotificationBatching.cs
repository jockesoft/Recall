using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddEpisodeNotificationBatching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_notification_user_id_type_episode_tvdb_id",
                table: "notification");

            migrationBuilder.AddColumn<int>(
                name: "episode_count",
                table: "notification",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "notified_episode",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    series_tvdb_id = table.Column<int>(type: "integer", nullable: false),
                    episode_tvdb_id = table.Column<int>(type: "integer", nullable: false),
                    created_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notified_episode", x => x.id);
                    table.ForeignKey(
                        name: "FK_notified_episode_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notified_episode_user_id_episode_tvdb_id",
                table: "notified_episode",
                columns: new[] { "user_id", "episode_tvdb_id" },
                unique: true);

            // Seed the ledger from notifications that already exist, so the sweep
            // doesn't re-notify about episodes users have already been told about.
            migrationBuilder.Sql(
                """
                INSERT INTO notified_episode (id, user_id, series_tvdb_id, episode_tvdb_id, created_utc)
                SELECT gen_random_uuid(), n.user_id, COALESCE(n.series_tvdb_id, 0), n.episode_tvdb_id, n.created_utc
                FROM notification n
                WHERE n.type = 'NewEpisode' AND n.episode_tvdb_id IS NOT NULL
                ON CONFLICT (user_id, episode_tvdb_id) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notified_episode");

            migrationBuilder.DropColumn(
                name: "episode_count",
                table: "notification");

            migrationBuilder.CreateIndex(
                name: "IX_notification_user_id_type_episode_tvdb_id",
                table: "notification",
                columns: new[] { "user_id", "type", "episode_tvdb_id" },
                unique: true,
                filter: "episode_tvdb_id IS NOT NULL");
        }
    }
}
