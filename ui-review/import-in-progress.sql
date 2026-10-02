-- An import that is still being matched, for the "import-watchlist_in-progress"
-- screenshot. Run by `review.sh import-progress on` against the review clone
-- (recall_uireview) just before the shot and removed by `... off` right after,
-- never against recall_db.
--
-- It is newer than the completed job from seed.sql, so it is the one the
-- import page shows while it exists. The waiting rows use IMDb ids that do not
-- exist: if the import job happens to run in the seconds this is in place, it
-- finds no match and adds nothing to the library.

\set dev '''11111111-1111-1111-1111-111111111111'''
\set job '''cccccccc-0000-4000-8000-000000000001'''

INSERT INTO watchlist_import_job (id, user_id, file_name, status, total_count, processed_count, imported_count,
                                  skipped_count, not_found_count, failed_count, created_utc, completed_utc)
VALUES (:job, :dev, 'ratings.csv', 'Processing', 40, 12, 9, 2, 1, 0, now(), NULL);

INSERT INTO watchlist_import_item (id, job_id, row_number, imdb_id, title, title_type, your_rating, status,
                                   resolved_tvdb_id, result_message, created_utc, processed_utc)
SELECT gen_random_uuid(), :job, v.n, v.imdb, v.title, v.type, v.rating, v.status, v.tvdb, v.message, now(), now()
FROM (VALUES
    (1,  'tt0903747', 'Breaking Bad', 'TV Series', 10, 'AlreadyInLibrary', 81189, 'Already in your library.'),
    (2,  'tt15398776', 'Oppenheimer', 'Movie', 9, 'AlreadyInLibrary', 287533, 'Already watched.'),
    (3,  'tt0113277', 'Heat', 'Movie', 9, 'Imported', 1305, 'Marked watched and rated 9/10.'),
    (4,  'tt0816692', 'Interstellar', 'Movie', 8, 'Imported', 131079, 'Marked watched and rated 8/10.'),
    (5,  'tt15239678', 'Dune: Part Two', 'Movie', 8, 'Imported', 290272, 'Marked watched and rated 8/10.'),
    (6,  'tt11280740', 'Severance', 'TV Series', 9, 'Imported', 371980, 'Added to your library.'),
    (7,  'tt7366338', 'Chernobyl', 'TV Mini Series', 10, 'Imported', 360893, 'Added to your library.'),
    (8,  'tt0185906', 'Band of Brothers', 'TV Mini Series', 10, 'Imported', 74205, 'Added to your library.'),
    (9,  'tt13406094', 'Adolescence', 'TV Mini Series', 8, 'Imported', 452467, 'Added to your library.'),
    (10, 'tt13649112', 'Baby Reindeer', 'TV Mini Series', 7, 'Imported', 417223, 'Added to your library.'),
    (11, 'tt10155688', 'Mare of Easttown', 'TV Mini Series', 8, 'Imported', 370112, 'Added to your library.'),
    (12, 'tt0000000', 'A Title Nobody Has Heard Of', 'Movie', 6, 'NotFound', NULL, 'No match on TheTVDB.')
) AS v(n, imdb, title, type, rating, status, tvdb, message);

-- Rows 13 to 40 are still waiting.
INSERT INTO watchlist_import_item (id, job_id, row_number, imdb_id, title, title_type, your_rating, status,
                                   resolved_tvdb_id, result_message, created_utc, processed_utc)
SELECT gen_random_uuid(), :job, n, 'tt99999' || n, 'Waiting title ' || (n - 12),
       CASE WHEN n % 3 = 0 THEN 'TV Series' ELSE 'Movie' END,
       CASE WHEN n % 4 = 0 THEN NULL ELSE 5 + n % 5 END,
       'Pending', NULL, NULL, now(), NULL
FROM generate_series(13, 40) AS n;
