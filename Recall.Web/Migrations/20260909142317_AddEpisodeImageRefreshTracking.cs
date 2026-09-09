using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddEpisodeImageRefreshTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "aired",
                table: "cached_episode_extended",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "has_image",
                table: "cached_episode_extended",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "refresh_attempts",
                table: "cached_episode_extended",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "aired",
                table: "cached_episode_extended");

            migrationBuilder.DropColumn(
                name: "has_image",
                table: "cached_episode_extended");

            migrationBuilder.DropColumn(
                name: "refresh_attempts",
                table: "cached_episode_extended");
        }
    }
}
