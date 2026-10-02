using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Web.Migrations
{
    /// <inheritdoc />
    public partial class SeriesStillCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "aired_episode_count",
                table: "cached_series_aggregate",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "aired_still_count",
                table: "cached_series_aggregate",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Fill the two counts for the rows already cached, from their
            // payload, by the rule TvdbSnapshotStore.StillCoverage uses for new
            // ones: regular episodes (not season 0, not movie-flagged) that have
            // aired, and how many of those have a still. A row with no aired
            // regular episode keeps 0 and 0.
            migrationBuilder.Sql(
                """
                UPDATE cached_series_aggregate a
                SET aired_episode_count = c.aired,
                    aired_still_count = c.with_still
                FROM (
                    SELECT s.tvdb_id, s.language,
                           count(*) AS aired,
                           count(*) FILTER (WHERE coalesce(e ->> 'image', '') <> '') AS with_still
                    FROM cached_series_aggregate s,
                         jsonb_array_elements(
                             CASE WHEN jsonb_typeof(s.payload -> 'episodes') = 'array'
                                  THEN s.payload -> 'episodes' ELSE '[]'::jsonb END) e
                    WHERE (e ->> 'seasonNumber') IS DISTINCT FROM '0'
                      AND (e ->> 'isMovie') IS DISTINCT FROM 'true'
                      AND (e ->> 'aired') ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
                      AND (e ->> 'aired')::date <= current_date
                    GROUP BY s.tvdb_id, s.language
                ) c
                WHERE c.tvdb_id = a.tvdb_id AND c.language = a.language;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "aired_episode_count",
                table: "cached_series_aggregate");

            migrationBuilder.DropColumn(
                name: "aired_still_count",
                table: "cached_series_aggregate");
        }
    }
}
