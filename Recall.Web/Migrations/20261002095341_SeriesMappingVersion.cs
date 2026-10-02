using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Web.Migrations
{
    /// <inheritdoc />
    public partial class SeriesMappingVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "mapping_version",
                table: "cached_series_aggregate",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Version 1 of the mapping is the one that keeps TheTVDB's genres.
            // A row that already has the "genres" property (even an empty list:
            // the series has none) was written by it; every other row stays at
            // 0 and is refreshed first by UpdateTvDbInfoTimer.
            migrationBuilder.Sql(
                """
                UPDATE cached_series_aggregate
                SET mapping_version = 1
                WHERE payload ? 'genres';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "mapping_version",
                table: "cached_series_aggregate");
        }
    }
}
