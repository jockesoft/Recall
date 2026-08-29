using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Web.Migrations
{
    /// <inheritdoc />
    public partial class EpisodeWatchTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "episode_watch",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    series_tvdb_id = table.Column<int>(type: "integer", nullable: false),
                    episode_tvdb_id = table.Column<int>(type: "integer", nullable: false),
                    watched_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_episode_watch", x => x.id);
                    table.ForeignKey(
                        name: "FK_episode_watch_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_episode_watch_user_id_episode_tvdb_id",
                table: "episode_watch",
                columns: new[] { "user_id", "episode_tvdb_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_episode_watch_user_id_series_tvdb_id",
                table: "episode_watch",
                columns: new[] { "user_id", "series_tvdb_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "episode_watch");
        }
    }
}
