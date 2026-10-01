using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Web.Migrations
{
    /// <inheritdoc />
    public partial class MoveImportedMovieLikesToWatchlist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data-only, one-off. Before the watchlist existed, the IMDb import
            // stood in for it by *liking* every unrated movie. Those likes are
            // moved to where they were always meant to go.
            //
            // An import-created like is recognisable from the import's own
            // record: an item with status 'Imported' and the result message
            // 'Added to your favorites.' (only the unrated-movie path ever wrote
            // that), whose resolved_tvdb_id names the movie.
            //
            // Moved: such a movie that the user still likes and has not watched.
            // Left alone: likes the user made themselves, a like the user has
            // since removed (nothing to move), and a movie since watched — it
            // can't go on a watchlist, and by now the like may be a real opinion.
            //
            // The watchlist entry keeps the like's timestamp, so "added on" is
            // when the import ran, and the import report is relabelled to match.
            migrationBuilder.Sql(
                """
                WITH imported AS (
                    SELECT j.user_id, i.resolved_tvdb_id AS tvdb_id, min(i.title) AS name
                    FROM watchlist_import_item i
                    JOIN watchlist_import_job j ON j.id = i.job_id
                    WHERE i.status = 'Imported'
                      AND i.result_message = 'Added to your favorites.'
                      AND i.resolved_tvdb_id IS NOT NULL
                    GROUP BY j.user_id, i.resolved_tvdb_id
                ),
                movable AS (
                    SELECT im.user_id, im.tvdb_id, im.name, l.id AS like_id, l.created_utc
                    FROM imported im
                    JOIN user_like l
                      ON l.user_id = im.user_id
                     AND l.target_type = 'Movie'
                     AND l.target_tvdb_id = im.tvdb_id
                    WHERE NOT EXISTS (
                        SELECT 1 FROM user_movie_watch w
                        WHERE w.user_id = im.user_id AND w.movie_tvdb_id = im.tvdb_id)
                ),
                added AS (
                    INSERT INTO tracked_movie (id, user_id, tvdb_id, name, created_utc, updated_utc)
                    SELECT gen_random_uuid(), user_id, tvdb_id, left(name, 500), created_utc, now()
                    FROM movable
                    ON CONFLICT (user_id, tvdb_id) DO NOTHING
                ),
                relabelled AS (
                    UPDATE watchlist_import_item i
                    SET result_message = 'Added to your watchlist.'
                    FROM watchlist_import_job j, movable m
                    WHERE j.id = i.job_id
                      AND j.user_id = m.user_id
                      AND i.resolved_tvdb_id = m.tvdb_id
                      AND i.status = 'Imported'
                      AND i.result_message = 'Added to your favorites.'
                )
                DELETE FROM user_like l
                USING movable m
                WHERE l.id = m.like_id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible: once moved, a watchlist entry that came from the
            // import can't be told apart from one the user added themselves.
        }
    }
}
