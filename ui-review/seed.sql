-- Seed for the UI review clone (recall_uireview). Run by review.sh setup, never
-- against recall_db. It tops up what the dev user already has so every page
-- has something to show: about 15 tracked series in different states, movies
-- on the watchlist and watched, likes, ratings, read and unread notifications,
-- a completed import, and a few series not watched in a while.
--
-- Everything is derived from the cached aggregates, so it depends on the
-- titles review.sh fetched (EXTRA_SERIES / EXTRA_MOVIES) and on the ones the
-- dev database already had cached.

\set dev '''11111111-1111-1111-1111-111111111111'''

-- ---- Helpers (dropped at the end) -----------------------------------------

CREATE FUNCTION ui_track(series int) RETURNS void LANGUAGE sql AS $$
    INSERT INTO tracked_series (id, user_id, tvdb_id, name, overview, image_url, first_aired, created_utc, updated_utc)
    SELECT gen_random_uuid(), '11111111-1111-1111-1111-111111111111', a.tvdb_id, left(a.name, 300),
           a.payload ->> 'overview', left(a.payload ->> 'imageUrl', 1000), (a.payload ->> 'firstAired')::date,
           now() - interval '20 days', now() - interval '20 days'
    FROM cached_series_aggregate a
    WHERE a.tvdb_id = series AND a.language = 'eng'
    ON CONFLICT (user_id, tvdb_id) DO NOTHING;
$$;

-- Marks the first `how_many` aired episodes watched, in season/episode order
-- with specials last; NULL means every aired episode. An episode without an
-- air date counts as aired, as it does in the app.
CREATE FUNCTION ui_watch(series int, how_many int DEFAULT NULL) RETURNS void LANGUAGE sql AS $$
    INSERT INTO episode_watch (id, user_id, series_tvdb_id, episode_tvdb_id, watched_utc, created_utc, updated_utc)
    SELECT gen_random_uuid(), '11111111-1111-1111-1111-111111111111', series, x.episode_id, x.watched, x.watched, x.watched
    FROM (
        SELECT (e ->> 'id')::int AS episode_id,
               now() - interval '30 days' + (row_number() OVER w) * interval '3 hours' AS watched,
               row_number() OVER w AS n
        FROM cached_series_aggregate a, jsonb_array_elements(a.payload -> 'episodes') e
        WHERE a.tvdb_id = series AND a.language = 'eng'
          AND coalesce((e ->> 'aired')::date, current_date) <= current_date
        WINDOW w AS (ORDER BY ((e ->> 'seasonNumber')::int = 0), (e ->> 'seasonNumber')::int, (e ->> 'episodeNumber')::int, (e ->> 'id')::int)
    ) x
    WHERE how_many IS NULL OR x.n <= how_many
    ON CONFLICT (user_id, episode_tvdb_id) DO NOTHING;
$$;

-- ---- Upcoming episodes -----------------------------------------------------
-- The Dashboard lists episodes airing in the next 30 days, and the cached data
-- has none. Two schedules are moved, in the clone only:
--   Silo: its announced future episodes air weekly, starting in two days.
--   Deadliest Catch: episodes from the last 14 days air 14 days later.

UPDATE cached_series_aggregate a
SET payload = jsonb_set(a.payload, '{episodes}', (
    SELECT jsonb_agg(
               CASE WHEN x.n IS NULL THEN x.e
                    ELSE jsonb_set(x.e, '{aired}', to_jsonb(to_char(current_date + (2 + 7 * (x.n - 1))::int, 'YYYY-MM-DD')))
               END ORDER BY x.ord)
    FROM (
        SELECT e, ord,
               CASE WHEN (e ->> 'aired')::date > current_date
                    THEN row_number() OVER (PARTITION BY ((e ->> 'aired')::date > current_date) ORDER BY (e ->> 'aired')::date, ord)
               END AS n
        FROM jsonb_array_elements(a.payload -> 'episodes') WITH ORDINALITY t(e, ord)
    ) x))
WHERE a.tvdb_id = 403245;

UPDATE cached_series_aggregate a
SET payload = jsonb_set(a.payload, '{episodes}', (
    SELECT jsonb_agg(
               CASE WHEN (e ->> 'aired')::date BETWEEN current_date - 14 AND current_date
                    THEN jsonb_set(e, '{aired}', to_jsonb(to_char((e ->> 'aired')::date + 14, 'YYYY-MM-DD')))
                    ELSE e
               END ORDER BY ord)
    FROM jsonb_array_elements(a.payload -> 'episodes') WITH ORDINALITY t(e, ord)))
WHERE a.tvdb_id = 78957;

-- ---- Series: 15 tracked, in every state ------------------------------------

-- Watched (ended, everything seen): Dark already is; add four more.
SELECT ui_track(360893), ui_watch(360893);   -- Chernobyl
SELECT ui_track(74205),  ui_watch(74205);    -- Band of Brothers
SELECT ui_track(452467), ui_watch(452467);   -- Adolescence
SELECT ui_track(417648), ui_watch(417648);   -- The Penguin

-- Up to date (continuing, everything aired seen).
SELECT ui_track(371980), ui_watch(371980);   -- Severance
SELECT ui_watch(403245);                     -- Silo (already tracked; has upcoming episodes)
SELECT ui_watch(426845);                     -- The Gentlemen (already tracked)

-- Watching: Breaking Bad, Deadliest Catch, Kitchen Nightmares, The Bear and
-- The Grand Tour already are. Add one in progress and one not started.
SELECT ui_track(417223), ui_watch(417223, 3); -- Baby Reindeer, 3 of 7
SELECT ui_track(370112);                      -- Mare of Easttown, nothing watched

-- ---- Movies ----------------------------------------------------------------
-- Watched already: Oppenheimer, The Ridiculous 6, Killer Elite.

INSERT INTO tracked_movie (id, user_id, tvdb_id, name, created_utc, updated_utc)
SELECT gen_random_uuid(), :dev, a.tvdb_id, left(a.name, 500), now() - (a.tvdb_id % 9) * interval '1 day', now()
FROM cached_movie_aggregate a
WHERE a.language = 'eng' AND a.tvdb_id IN (1305, 131079, 782)   -- Heat, Interstellar, Lethal Weapon
ON CONFLICT (user_id, tvdb_id) DO NOTHING;

INSERT INTO user_movie_watch (id, user_id, movie_tvdb_id, watched_utc, created_utc, updated_utc)
VALUES (gen_random_uuid(), :dev, 290272, now() - interval '6 days', now(), now())   -- Dune: Part Two
ON CONFLICT (user_id, movie_tvdb_id) DO NOTHING;

-- ---- Likes and ratings -----------------------------------------------------

INSERT INTO user_like (id, user_id, target_type, target_tvdb_id, series_tvdb_id, created_utc, updated_utc)
SELECT gen_random_uuid(), :dev, v.type, v.id, v.id, now() - v.days * interval '1 day', now()
FROM (VALUES ('Series', 371980, 1), ('Series', 360893, 3), ('Series', 74205, 8), ('Series', 81189, 12),
             ('Movie', 131079, 2), ('Movie', 290272, 5)) AS v(type, id, days)
ON CONFLICT (user_id, target_type, target_tvdb_id) DO NOTHING;

-- A few liked episodes: the first regular episode of three series.
INSERT INTO user_like (id, user_id, target_type, target_tvdb_id, series_tvdb_id, created_utc, updated_utc)
SELECT gen_random_uuid(), :dev, 'Episode', (e ->> 'id')::int, a.tvdb_id, now() - interval '4 days', now()
FROM cached_series_aggregate a, jsonb_array_elements(a.payload -> 'episodes') e
WHERE a.tvdb_id IN (371980, 360893, 74205) AND a.language = 'eng'
  AND (e ->> 'seasonNumber')::int = 1 AND (e ->> 'episodeNumber')::int = 1
ON CONFLICT (user_id, target_type, target_tvdb_id) DO NOTHING;

INSERT INTO user_rating (id, user_id, target_type, target_tvdb_id, series_tvdb_id, value, created_utc, updated_utc)
SELECT gen_random_uuid(), :dev, v.type, v.id, v.id, v.value, now() - interval '3 days', now()
FROM (VALUES ('Series', 371980, 9), ('Series', 360893, 10), ('Series', 74205, 10), ('Series', 81189, 9),
             ('Series', 334824, 8), ('Series', 452467, 8), ('Series', 417648, 7), ('Series', 426845, 6),
             ('Movie', 131079, 9), ('Movie', 290272, 8)) AS v(type, id, value)
ON CONFLICT (user_id, target_type, target_tvdb_id) DO NOTHING;

-- Two other members, so community averages differ from the dev user's own score.
INSERT INTO app_user (id, user_name, email, role, created_utc, updated_utc)
VALUES ('22222222-2222-2222-2222-222222222222', 'saga', 'saga@example.com', 'User', now(), now()),
       ('33333333-3333-3333-3333-333333333333', 'martin', 'martin@example.com', 'User', now(), now())
ON CONFLICT DO NOTHING;

INSERT INTO user_rating (id, user_id, target_type, target_tvdb_id, series_tvdb_id, value, created_utc, updated_utc)
SELECT gen_random_uuid(), v.user_id::uuid, v.type, v.id, v.id, v.value, now(), now()
FROM (VALUES ('22222222-2222-2222-2222-222222222222', 'Series', 371980, 8),
             ('33333333-3333-3333-3333-333333333333', 'Series', 371980, 7),
             ('22222222-2222-2222-2222-222222222222', 'Series', 81189, 10),
             ('33333333-3333-3333-3333-333333333333', 'Series', 360893, 8),
             ('22222222-2222-2222-2222-222222222222', 'Movie', 131079, 7),
             ('33333333-3333-3333-3333-333333333333', 'Movie', 287533, 9)) AS v(user_id, type, id, value)
ON CONFLICT (user_id, target_type, target_tvdb_id) DO NOTHING;

-- ---- Notifications: read and unread ----------------------------------------
-- The existing ones are all unread. Mark the older ones read and add two
-- recent unread ones.

UPDATE notification
SET is_read = true, read_utc = created_utc + interval '2 hours'
WHERE user_id = :dev
  AND id NOT IN (SELECT id FROM notification WHERE user_id = :dev ORDER BY created_utc DESC LIMIT 2);

INSERT INTO notification (id, user_id, type, title, body, series_tvdb_id, episode_tvdb_id, episode_count, is_read, created_utc, updated_utc)
SELECT gen_random_uuid(), :dev, 'NewEpisode', v.title, v.body, v.series,
       (SELECT (e ->> 'id')::int FROM cached_series_aggregate a, jsonb_array_elements(a.payload -> 'episodes') e
        WHERE a.tvdb_id = v.series AND (e ->> 'seasonNumber')::int > 0
        ORDER BY (e ->> 'seasonNumber')::int DESC, (e ->> 'episodeNumber')::int DESC LIMIT 1),
       v.count, false, now() - v.hours * interval '1 hour', now()
FROM (VALUES ('New episode of The Bear', 'S05E10 is out now.', 403294, 1, 3),
             ('3 new episodes of Breaking Bad', 'S05 · E14–E16 are out now.', 81189, 3, 26)) AS v(title, body, series, count, hours);

-- ---- A completed import ----------------------------------------------------
-- Newer than the existing two-row job, so it is the one the import page shows.

WITH job AS (
    INSERT INTO watchlist_import_job (id, user_id, file_name, status, total_count, processed_count, imported_count,
                                      skipped_count, not_found_count, failed_count, created_utc, completed_utc)
    VALUES (gen_random_uuid(), :dev, 'WATCHLIST.csv', 'Completed', 12, 12, 6, 4, 1, 1,
            now() - interval '2 hours', now() - interval '110 minutes')
    RETURNING id)
INSERT INTO watchlist_import_item (id, job_id, row_number, imdb_id, title, title_type, your_rating, status,
                                   resolved_tvdb_id, result_message, created_utc, processed_utc)
SELECT gen_random_uuid(), job.id, v.n, v.imdb, v.title, v.type, v.rating, v.status, v.tvdb, v.message,
       now() - interval '2 hours', now() - interval '110 minutes'
FROM job, (VALUES
    (1,  'tt0113277', 'Heat', 'Movie', NULL, 'Imported', 1305, 'Added to your watchlist.'),
    (2,  'tt0816692', 'Interstellar', 'Movie', NULL, 'Imported', 131079, 'Added to your watchlist.'),
    (3,  'tt15239678', 'Dune: Part Two', 'Movie', 8, 'Imported', 290272, 'Marked watched and rated 8/10.'),
    (4,  'tt11280740', 'Severance', 'TV Series', 9, 'Imported', 371980, 'Added to your library.'),
    (5,  'tt7366338', 'Chernobyl', 'TV Mini Series', 10, 'Imported', 360893, 'Added to your library.'),
    (6,  'tt0185906', 'Band of Brothers', 'TV Mini Series', 10, 'Imported', 74205, 'Added to your library.'),
    (7,  'tt0903747', 'Breaking Bad', 'TV Series', NULL, 'AlreadyInLibrary', 81189, 'Already in your library.'),
    (8,  'tt15398776', 'Oppenheimer', 'Movie', NULL, 'AlreadyInLibrary', 287533, 'Already watched.'),
    (9,  'tt0093409', 'Lethal Weapon', 'Movie', NULL, 'AlreadyInLibrary', 782, 'Already on your watchlist.'),
    (10, 'tt9999991', 'An Obscure Short Film With a Rather Long Title That Wraps', 'Movie', NULL, 'NotFound', NULL, 'No match on TheTVDB.'),
    (11, 'tt9999992', 'Some Podcast Episode', 'Podcast Episode', NULL, 'Unsupported', NULL, '"Podcast Episode" isn''t a supported title type.'),
    (12, 'tt9999993', 'The Wire', 'TV Series', 10, 'Failed', NULL, 'TheTVDB did not answer. Try the import again later.')
  ) AS v(n, imdb, title, type, rating, status, tvdb, message);

-- ---- Haven't watched in a while ---------------------------------------------
-- Watch dates are spread so the queue has both an active list and a dormant
-- group (Library:DormantAfterDays is 90): two series last watched months ago,
-- and one never started that was added four months ago. The rest of the queue
-- keeps its recent dates.

UPDATE episode_watch SET watched_utc = watched_utc - interval '200 days'
WHERE user_id = :dev AND series_tvdb_id = 314087;   -- The Grand Tour (2016)

UPDATE episode_watch SET watched_utc = watched_utc - interval '130 days'
WHERE user_id = :dev AND series_tvdb_id = 417223;   -- Baby Reindeer

UPDATE tracked_series SET created_utc = now() - interval '120 days'
WHERE user_id = :dev AND tvdb_id = 370112;          -- Mare of Easttown, nothing watched

DROP FUNCTION ui_track(int);
DROP FUNCTION ui_watch(int, int);
