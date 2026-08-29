using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Web.Migrations
{
    /// <inheritdoc />
    public partial class CachedTvdbSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cached_episode_extended",
                columns: table => new
                {
                    episode_tvdb_id = table.Column<int>(type: "integer", nullable: false),
                    series_tvdb_id = table.Column<int>(type: "integer", nullable: true),
                    name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    retrieved_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cached_episode_extended", x => x.episode_tvdb_id);
                });

            migrationBuilder.CreateTable(
                name: "cached_series_aggregate",
                columns: table => new
                {
                    tvdb_id = table.Column<int>(type: "integer", nullable: false),
                    language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    keep_updated = table.Column<bool>(type: "boolean", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    retrieved_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cached_series_aggregate", x => new { x.tvdb_id, x.language });
                });

            migrationBuilder.CreateTable(
                name: "cached_series_extended",
                columns: table => new
                {
                    tvdb_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    retrieved_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cached_series_extended", x => x.tvdb_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cached_episode_extended");

            migrationBuilder.DropTable(
                name: "cached_series_aggregate");

            migrationBuilder.DropTable(
                name: "cached_series_extended");
        }
    }
}
