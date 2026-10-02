using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Web.Migrations
{
    /// <inheritdoc />
    public partial class WatchSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "user_movie_watch",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "episode_watch",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Unknown");

            // Every existing row is now 'Unknown': the column did not exist when
            // it was written. Two kinds can still be recognised, and are, so
            // that Stats does not chart a catch-up or an import as a day of
            // watching. Nothing else about the rows changes (updated_utc is
            // left alone: the watch itself was not edited).

            // 1. A movie the IMDb import marked watched: the user has an import
            //    row that resolved to it and was processed within a minute of
            //    the watch. (The importer writes the watch, then the row's
            //    result, milliseconds apart. A movie that was already watched
            //    when the import ran has an older watch and is not matched.)
            migrationBuilder.Sql(
                """
                UPDATE user_movie_watch w
                SET source = 'Import'
                WHERE EXISTS (
                    SELECT 1
                    FROM watchlist_import_item i
                    JOIN watchlist_import_job j ON j.id = i.job_id
                    WHERE j.user_id = w.user_id
                      AND i.resolved_tvdb_id = w.movie_tvdb_id
                      AND i.status = 'Imported'
                      AND i.processed_utc IS NOT NULL
                      AND i.processed_utc BETWEEN w.watched_utc - interval '1 minute'
                                              AND w.watched_utc + interval '1 minute');
                """);

            // 2. Episodes marked in bulk: three or more watches by one user in
            //    one series, each within two minutes of the one before. Two in
            //    a row are left as they are: marking two episodes after an
            //    evening's watching is ordinary.
            migrationBuilder.Sql(
                """
                WITH ordered AS (
                    SELECT id, user_id, series_tvdb_id, watched_utc,
                           CASE WHEN watched_utc - lag(watched_utc) OVER run <= interval '2 minutes'
                                THEN 0 ELSE 1 END AS starts_run
                    FROM episode_watch
                    WINDOW run AS (PARTITION BY user_id, series_tvdb_id ORDER BY watched_utc, id)
                ),
                numbered AS (
                    SELECT id, user_id, series_tvdb_id,
                           sum(starts_run) OVER (PARTITION BY user_id, series_tvdb_id ORDER BY watched_utc, id) AS run_no
                    FROM ordered
                ),
                sized AS (
                    SELECT id, count(*) OVER (PARTITION BY user_id, series_tvdb_id, run_no) AS run_size
                    FROM numbered
                )
                UPDATE episode_watch e
                SET source = 'Bulk'
                FROM sized s
                WHERE s.id = e.id
                  AND s.run_size >= 3;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source",
                table: "user_movie_watch");

            migrationBuilder.DropColumn(
                name: "source",
                table: "episode_watch");
        }
    }
}
