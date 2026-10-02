-- A watch history that was only ever marked in bulk or imported, for the
-- "stats_sparse" screenshot. Run by `review.sh stats-sparse on` against the
-- empty review clone (recall_uireview_empty) just before the shot and removed
-- by `... off` right after, never against recall_db.
--
-- It is what the Stats page shows someone who caught up on two series in one
-- sitting and brought two movies in from IMDb: totals, top series, genres, and
-- an explanation where the month-by-month chart would be. The titles are ones
-- the dev database has cached (the clone keeps the cache tables).

\set dev '''11111111-1111-1111-1111-111111111111'''

-- Dark (ended) and Breaking Bad, every aired episode, each series at one instant.
INSERT INTO episode_watch (id, user_id, series_tvdb_id, episode_tvdb_id, watched_utc, source, created_utc, updated_utc)
SELECT gen_random_uuid(), :dev, a.tvdb_id, (e ->> 'id')::int,
       date_trunc('minute', now()) - interval '40 days', 'Bulk', now(), now()
FROM cached_series_aggregate a, jsonb_array_elements(a.payload -> 'episodes') e
WHERE a.language = 'eng' AND a.tvdb_id IN (334824, 81189)
  AND coalesce((e ->> 'aired')::date, current_date) <= current_date
ON CONFLICT (user_id, episode_tvdb_id) DO NOTHING;

-- In the library, so Dark counts as a series finished.
INSERT INTO tracked_series (id, user_id, tvdb_id, name, overview, image_url, first_aired, created_utc, updated_utc)
SELECT gen_random_uuid(), :dev, a.tvdb_id, left(a.name, 300), a.payload ->> 'overview',
       left(a.payload ->> 'imageUrl', 1000), (a.payload ->> 'firstAired')::date, now(), now()
FROM cached_series_aggregate a
WHERE a.language = 'eng' AND a.tvdb_id IN (334824, 81189)
ON CONFLICT (user_id, tvdb_id) DO NOTHING;

-- Oppenheimer and Killer Elite, as the IMDb import marks a rated movie.
INSERT INTO user_movie_watch (id, user_id, movie_tvdb_id, watched_utc, source, created_utc, updated_utc)
SELECT gen_random_uuid(), :dev, a.tvdb_id, now() - interval '12 days', 'Import', now(), now()
FROM cached_movie_aggregate a
WHERE a.language = 'eng' AND a.tvdb_id IN (287533, 2039)
ON CONFLICT (user_id, movie_tvdb_id) DO NOTHING;
