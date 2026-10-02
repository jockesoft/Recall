using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Web.Migrations
{
    /// <inheritdoc />
    public partial class DropEpisodeRefreshAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "refresh_attempts",
                table: "cached_episode_extended");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "refresh_attempts",
                table: "cached_episode_extended",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
