-- More than nine series in Continue watching, for the "dashboard_continue-
-- watching-expanded" screenshot ("Show all N" pressed). Run by
-- `review.sh continue-watching-many on` against the review clone
-- (recall_uireview) just before the shot and undone by `... off` right after,
-- never against recall_db.
--
-- The seed's finished and up-to-date series are taken back into the queue:
-- each loses the watch of its last regular episode (by season and number in
-- the cached aggregate, so never a special), and the rest of its watches are
-- moved to the last few hours, so it counts as watched recently and is not in
-- the "haven't watched in a while" group. Their rows are kept in a table of
-- their own first, which `off` puts back.

\set dev '''11111111-1111-1111-1111-111111111111'''

CREATE TABLE ui_review_cw_backup AS
SELECT * FROM episode_watch
WHERE user_id = :dev
  AND series_tvdb_id IN (334824, 360893, 74205, 452467, 417648, 371980, 403245);

WITH last_regular AS (
    SELECT DISTINCT ON (w.series_tvdb_id) w.id
    FROM episode_watch w
    JOIN cached_series_aggregate a ON a.tvdb_id = w.series_tvdb_id AND a.language = 'eng'
    CROSS JOIN LATERAL jsonb_array_elements(a.payload -> 'episodes') e
    WHERE w.id IN (SELECT id FROM ui_review_cw_backup)
      AND (e ->> 'id')::int = w.episode_tvdb_id
      AND coalesce((e ->> 'seasonNumber')::int, 0) > 0
    ORDER BY w.series_tvdb_id, (e ->> 'seasonNumber')::int DESC, (e ->> 'episodeNumber')::int DESC)
DELETE FROM episode_watch
WHERE id IN (SELECT id FROM last_regular);

WITH ranked AS (
    SELECT series_tvdb_id, row_number() OVER (ORDER BY series_tvdb_id) AS n
    FROM (SELECT DISTINCT series_tvdb_id FROM ui_review_cw_backup) s)
UPDATE episode_watch w
SET watched_utc = now() - r.n * interval '1 hour'
FROM ranked r
WHERE w.user_id = :dev AND w.series_tvdb_id = r.series_tvdb_id;
