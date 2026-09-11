using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCachedEpisodeOmdb : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cached_episode_omdb",
                columns: table => new
                {
                    tvdb_id = table.Column<int>(type: "integer", nullable: false),
                    imdb_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: true),
                    retrieved_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cached_episode_omdb", x => x.tvdb_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cached_episode_omdb_retrieved_utc",
                table: "cached_episode_omdb",
                column: "retrieved_utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cached_episode_omdb");
        }
    }
}
