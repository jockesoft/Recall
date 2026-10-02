using AwesomeAssertions;

namespace Recall.Tests.Postgres.Migrations;

/// <summary>
/// The migrations that move or erase data. Each test stops at the migration
/// before the one under test, seeds rows as they looked at that point, applies
/// the rest, and checks what happened to them.
/// </summary>
[TestFixture]
public sealed class DataMigrationTests
{
    private const string BeforeNotificationBatching = "20260903134754_AddNotifications";
    private const string NotificationBatching = "20260903135946_AddEpisodeNotificationBatching";
    private const string BeforeClearSentEmailBodies = "20260912124139_AddWatchlistImport";
    private const string ClearSentEmailBodies = "20261001142833_ClearSentEmailBodies";
    private const string ClearAbandonedEmailBodies = "20261001145210_ClearAbandonedEmailBodies";
    private const string BeforeMoveImportedLikes = "20261001150244_AddTrackedMovie";
    private const string BeforeWatchSource = "20261002075311_WeeklyDigest";
    private const string WatchSource = "20261002085341_WatchSource";
    private const string SeriesMappingVersion = "20261002095341_SeriesMappingVersion";

    private static readonly DateTime ImportedUtc = new(2026, 9, 14, 8, 30, 0, DateTimeKind.Utc);

    // ---- AddEpisodeNotificationBatching: seed the notified_episode ledger ----

    [Test]
    public async Task NotificationBatching_Should_SeedTheLedgerFromExistingNotifications()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        await database.MigrateToAsync(BeforeNotificationBatching);

        var user = await database.InsertUserAsync();
        await InsertNotificationAsync(database, user, "NewEpisode", seriesId: 50, episodeId: 501);
        await InsertNotificationAsync(database, user, "NewEpisode", seriesId: null, episodeId: 502);
        await InsertNotificationAsync(database, user, "NewEpisode", seriesId: 50, episodeId: null);
        await InsertNotificationAsync(database, user, "Other", seriesId: 50, episodeId: 503);

        await database.MigrateToAsync(NotificationBatching);

        (await database.ScalarAsync<long>("SELECT count(*) FROM notified_episode WHERE user_id = $1", user))
            .Should().Be(2, "only NewEpisode notifications that name an episode are carried over");
        (await database.ScalarAsync<int>(
                "SELECT series_tvdb_id FROM notified_episode WHERE user_id = $1 AND episode_tvdb_id = 501", user))
            .Should().Be(50);
        (await database.ScalarAsync<int>(
                "SELECT series_tvdb_id FROM notified_episode WHERE user_id = $1 AND episode_tvdb_id = 502", user))
            .Should().Be(0, "a notification without a series is recorded against series 0");
        (await database.ScalarAsync<long>("SELECT count(*) FROM notification WHERE episode_count <> 1"))
            .Should().Be(0, "existing notifications each stand for one episode");
    }

    // ---- ClearSentEmailBodies ----

    [Test]
    public async Task ClearSentEmailBodies_Should_EraseDeliveredMessages_AndLeaveQueuedOnes()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        await database.MigrateToAsync(BeforeClearSentEmailBodies);

        var sent = await InsertEmailAsync(database, "Sign-in link 1", "<p>link 1</p>", sent: true, attempts: 1);
        var sentTextOnly = await InsertEmailAsync(database, "Sign-in link 2", null, sent: true, attempts: 1);
        var queued = await InsertEmailAsync(database, "Sign-in link 3", "<p>link 3</p>", sent: false, attempts: 0);

        await database.MigrateToAsync(ClearSentEmailBodies);

        (await EmailAsync(database, sent)).Should().Be(("", null));
        (await EmailAsync(database, sentTextOnly)).Should().Be(("", null));
        (await EmailAsync(database, queued)).Should().Be(("Sign-in link 3", "<p>link 3</p>"));

        // The envelope is kept: only the content goes.
        (await database.ScalarAsync<string>("SELECT subject FROM email WHERE id = $1", sent)).Should().Be("Subject");
        (await database.ScalarAsync<string>("SELECT to_address FROM email WHERE id = $1", sent))
            .Should().Be("someone@example.com");
    }

    [Test]
    public async Task ClearSentEmailBodies_Should_LeaveFailedMessagesForTheNextMigration()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        await database.MigrateToAsync(BeforeClearSentEmailBodies);

        var abandoned = await InsertEmailAsync(database, "Sign-in link", "<p>link</p>", sent: false, attempts: 5);

        await database.MigrateToAsync(ClearSentEmailBodies);

        (await EmailAsync(database, abandoned)).Should().Be(("Sign-in link", "<p>link</p>"));
    }

    // ---- ClearAbandonedEmailBodies ----

    [Test]
    public async Task ClearAbandonedEmailBodies_Should_EraseMessagesThatUsedUpTheirAttempts()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        await database.MigrateToAsync(ClearSentEmailBodies);

        var atLimit = await InsertEmailAsync(database, "Sign-in link 1", "<p>link 1</p>", sent: false, attempts: 5);
        var overLimit = await InsertEmailAsync(database, "Sign-in link 2", null, sent: false, attempts: 7);
        var stillRetrying = await InsertEmailAsync(database, "Sign-in link 3", "<p>link 3</p>", sent: false, attempts: 4);
        var neverTried = await InsertEmailAsync(database, "Sign-in link 4", "<p>link 4</p>", sent: false, attempts: 0);

        await database.MigrateToAsync(ClearAbandonedEmailBodies);

        (await EmailAsync(database, atLimit)).Should().Be(("", null));
        (await EmailAsync(database, overLimit)).Should().Be(("", null));
        (await EmailAsync(database, stillRetrying)).Should().Be(("Sign-in link 3", "<p>link 3</p>"));
        (await EmailAsync(database, neverTried)).Should().Be(("Sign-in link 4", "<p>link 4</p>"));

        (await database.ScalarAsync<int>("SELECT send_attempts FROM email WHERE id = $1", atLimit))
            .Should().Be(5, "the attempt count is the record of why it was given up on");
    }

    // ---- MoveImportedMovieLikesToWatchlist ----

    [Test]
    public async Task MoveImportedLikes_Should_MoveAnImportCreatedLikeToTheWatchlist()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        await database.MigrateToAsync(BeforeMoveImportedLikes);

        var user = await database.InsertUserAsync();
        var job = await InsertImportJobAsync(database, user);
        var item = await InsertImportItemAsync(database, job, "Heat", tvdbId: 1001);
        await InsertLikeAsync(database, user, "Movie", 1001, ImportedUtc);

        await database.MigrateToLatestAsync();

        (await LikeCountAsync(database, user, "Movie", 1001)).Should().Be(0);
        (await database.ScalarAsync<string>(
                "SELECT name FROM tracked_movie WHERE user_id = $1 AND tvdb_id = 1001", user))
            .Should().Be("Heat");
        (await database.ScalarAsync<DateTime>(
                "SELECT created_utc FROM tracked_movie WHERE user_id = $1 AND tvdb_id = 1001", user))
            .Should().Be(ImportedUtc, "the watchlist entry keeps the date the import ran");
        (await ResultMessageAsync(database, item)).Should().Be("Added to your watchlist.");
    }

    [Test]
    public async Task MoveImportedLikes_Should_LeaveLikesTheUserMadeThemselves()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        await database.MigrateToAsync(BeforeMoveImportedLikes);

        var importer = await database.InsertUserAsync();
        var other = await database.InsertUserAsync();
        var job = await InsertImportJobAsync(database, importer);
        await InsertImportItemAsync(database, job, "Heat", tvdbId: 1001);
        await InsertLikeAsync(database, importer, "Movie", 1001, ImportedUtc);

        // Not from the import: another movie, the same id as a series, and the
        // same movie liked by somebody else.
        await InsertLikeAsync(database, importer, "Movie", 1002, ImportedUtc);
        await InsertLikeAsync(database, importer, "Series", 1001, ImportedUtc);
        await InsertLikeAsync(database, other, "Movie", 1001, ImportedUtc);

        await database.MigrateToLatestAsync();

        (await LikeCountAsync(database, importer, "Movie", 1002)).Should().Be(1);
        (await LikeCountAsync(database, importer, "Series", 1001)).Should().Be(1);
        (await LikeCountAsync(database, other, "Movie", 1001)).Should().Be(1);
        (await database.ScalarAsync<long>("SELECT count(*) FROM tracked_movie")).Should().Be(1);
        (await database.ScalarAsync<long>("SELECT count(*) FROM tracked_movie WHERE user_id = $1", other))
            .Should().Be(0);
    }

    [Test]
    public async Task MoveImportedLikes_Should_LeaveAMovieTheUserHasSinceWatched()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        await database.MigrateToAsync(BeforeMoveImportedLikes);

        var user = await database.InsertUserAsync();
        var job = await InsertImportJobAsync(database, user);
        var item = await InsertImportItemAsync(database, job, "Heat", tvdbId: 1001);
        await InsertLikeAsync(database, user, "Movie", 1001, ImportedUtc);
        await database.ExecuteAsync(
            """
            INSERT INTO user_movie_watch (id, user_id, movie_tvdb_id, watched_utc, created_utc, updated_utc)
            VALUES (gen_random_uuid(), $1, 1001, now(), now(), now());
            """,
            user);

        await database.MigrateToLatestAsync();

        (await LikeCountAsync(database, user, "Movie", 1001)).Should().Be(1);
        (await database.ScalarAsync<long>("SELECT count(*) FROM tracked_movie")).Should().Be(0);
        (await ResultMessageAsync(database, item)).Should().Be("Added to your favorites.");
    }

    [Test]
    public async Task MoveImportedLikes_Should_DoNothing_WhenTheLikeWasAlreadyRemoved()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        await database.MigrateToAsync(BeforeMoveImportedLikes);

        var user = await database.InsertUserAsync();
        var job = await InsertImportJobAsync(database, user);
        var item = await InsertImportItemAsync(database, job, "Heat", tvdbId: 1001);

        await database.MigrateToLatestAsync();

        (await database.ScalarAsync<long>("SELECT count(*) FROM tracked_movie")).Should().Be(0);
        (await ResultMessageAsync(database, item)).Should().Be("Added to your favorites.");
    }

    [Test]
    public async Task MoveImportedLikes_Should_IgnoreImportRowsThatDidNotCreateALike()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        await database.MigrateToAsync(BeforeMoveImportedLikes);

        var user = await database.InsertUserAsync();
        var job = await InsertImportJobAsync(database, user);

        // A rated movie was marked watched and rated, not liked; the like on it is the user's own.
        var rated = await InsertImportItemAsync(database, job, "Heat", tvdbId: 1001, message: "Marked as watched and rated 8.");
        await InsertLikeAsync(database, user, "Movie", 1001, ImportedUtc);

        // Same message, but the row never resolved to a movie.
        var skipped = await InsertImportItemAsync(
            database, job, "Ronin", tvdbId: null, status: "AlreadyInLibrary", message: "Added to your favorites.");

        await database.MigrateToLatestAsync();

        (await LikeCountAsync(database, user, "Movie", 1001)).Should().Be(1);
        (await database.ScalarAsync<long>("SELECT count(*) FROM tracked_movie")).Should().Be(0);
        (await ResultMessageAsync(database, rated)).Should().Be("Marked as watched and rated 8.");
        (await ResultMessageAsync(database, skipped)).Should().Be("Added to your favorites.");
    }

    [Test]
    public async Task MoveImportedLikes_Should_CreateOneEntry_WhenTheMovieWasImportedTwice()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        await database.MigrateToAsync(BeforeMoveImportedLikes);

        var user = await database.InsertUserAsync();
        var firstJob = await InsertImportJobAsync(database, user);
        var secondJob = await InsertImportJobAsync(database, user);
        var first = await InsertImportItemAsync(database, firstJob, "Heat", tvdbId: 1001);
        var second = await InsertImportItemAsync(database, secondJob, "Heat (1995)", tvdbId: 1001);
        await InsertLikeAsync(database, user, "Movie", 1001, ImportedUtc);

        await database.MigrateToLatestAsync();

        (await database.ScalarAsync<long>("SELECT count(*) FROM tracked_movie WHERE user_id = $1", user))
            .Should().Be(1);
        (await LikeCountAsync(database, user, "Movie", 1001)).Should().Be(0);
        (await ResultMessageAsync(database, first)).Should().Be("Added to your watchlist.");
        (await ResultMessageAsync(database, second)).Should().Be("Added to your watchlist.");
    }

    [Test]
    public async Task MoveImportedLikes_Should_KeepAnExistingWatchlistEntry_AndStillRemoveTheLike()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        await database.MigrateToAsync(BeforeMoveImportedLikes);

        var user = await database.InsertUserAsync();
        var job = await InsertImportJobAsync(database, user);
        await InsertImportItemAsync(database, job, "Heat", tvdbId: 1001);
        await InsertLikeAsync(database, user, "Movie", 1001, ImportedUtc);
        await database.ExecuteAsync(
            """
            INSERT INTO tracked_movie (id, user_id, tvdb_id, name, created_utc, updated_utc)
            VALUES (gen_random_uuid(), $1, 1001, 'Already here', now(), now());
            """,
            user);

        await database.MigrateToLatestAsync();

        (await database.ScalarAsync<string>(
                "SELECT string_agg(name, ',') FROM tracked_movie WHERE user_id = $1 AND tvdb_id = 1001", user))
            .Should().Be("Already here");
        (await LikeCountAsync(database, user, "Movie", 1001)).Should().Be(0);
    }

    // ---- WatchSource: classify the rows that existed before the column ----

    private static readonly DateTime Evening = new(2026, 9, 20, 19, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task WatchSource_Should_MarkARunOfThreeOrMoreEpisodeWatchesAsBulk()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        await database.MigrateToAsync(BeforeWatchSource);
        var user = await database.InsertUserAsync();

        // A season marked in one go: the same instant.
        await InsertEpisodeWatchAsync(database, user, seriesId: 10, episodeId: 101, Evening);
        await InsertEpisodeWatchAsync(database, user, seriesId: 10, episodeId: 102, Evening);
        await InsertEpisodeWatchAsync(database, user, seriesId: 10, episodeId: 103, Evening);

        // Ticked off one after another, each within two minutes of the one
        // before, though the first and last are more than two minutes apart.
        await InsertEpisodeWatchAsync(database, user, seriesId: 20, episodeId: 201, Evening);
        await InsertEpisodeWatchAsync(database, user, seriesId: 20, episodeId: 202, Evening.AddSeconds(90));
        await InsertEpisodeWatchAsync(database, user, seriesId: 20, episodeId: 203, Evening.AddSeconds(180));
        await InsertEpisodeWatchAsync(database, user, seriesId: 20, episodeId: 204, Evening.AddSeconds(270));

        await database.MigrateToAsync(WatchSource);

        foreach (var episode in new[] { 101, 102, 103, 201, 202, 203, 204 })
            (await EpisodeSourceAsync(database, user, episode)).Should().Be("Bulk", $"episode {episode} is part of a run");
    }

    [Test]
    public async Task WatchSource_Should_LeaveOrdinaryEpisodeWatchesAsUnknown()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        await database.MigrateToAsync(BeforeWatchSource);
        var user = await database.InsertUserAsync();
        var other = await database.InsertUserAsync();

        // One episode an evening.
        await InsertEpisodeWatchAsync(database, user, seriesId: 10, episodeId: 101, Evening);
        await InsertEpisodeWatchAsync(database, user, seriesId: 10, episodeId: 102, Evening.AddDays(1));
        await InsertEpisodeWatchAsync(database, user, seriesId: 10, episodeId: 103, Evening.AddDays(2));

        // Two marked together after a double bill: a pair is not a run.
        await InsertEpisodeWatchAsync(database, user, seriesId: 20, episodeId: 201, Evening);
        await InsertEpisodeWatchAsync(database, user, seriesId: 20, episodeId: 202, Evening.AddSeconds(20));

        // Three in one evening, each an episode's length after the one before.
        await InsertEpisodeWatchAsync(database, user, seriesId: 30, episodeId: 301, Evening);
        await InsertEpisodeWatchAsync(database, user, seriesId: 30, episodeId: 302, Evening.AddMinutes(61));
        await InsertEpisodeWatchAsync(database, user, seriesId: 30, episodeId: 303, Evening.AddMinutes(122));

        // The same instant, but three different series, or two different users.
        await InsertEpisodeWatchAsync(database, user, seriesId: 40, episodeId: 401, Evening.AddDays(5));
        await InsertEpisodeWatchAsync(database, user, seriesId: 41, episodeId: 411, Evening.AddDays(5));
        await InsertEpisodeWatchAsync(database, user, seriesId: 42, episodeId: 421, Evening.AddDays(5));
        await InsertEpisodeWatchAsync(database, user, seriesId: 50, episodeId: 501, Evening.AddDays(6));
        await InsertEpisodeWatchAsync(database, user, seriesId: 50, episodeId: 502, Evening.AddDays(6));
        await InsertEpisodeWatchAsync(database, other, seriesId: 50, episodeId: 503, Evening.AddDays(6));

        await database.MigrateToAsync(WatchSource);

        (await database.ScalarAsync<long>("SELECT count(*) FROM episode_watch WHERE source <> 'Unknown'"))
            .Should().Be(0);
        (await database.ScalarAsync<long>("SELECT count(*) FROM episode_watch")).Should().Be(14);
    }

    [Test]
    public async Task WatchSource_Should_SplitARunFromTheWatchesAroundIt()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        await database.MigrateToAsync(BeforeWatchSource);
        var user = await database.InsertUserAsync();

        await InsertEpisodeWatchAsync(database, user, seriesId: 10, episodeId: 101, Evening.AddDays(-1));
        await InsertEpisodeWatchAsync(database, user, seriesId: 10, episodeId: 102, Evening);
        await InsertEpisodeWatchAsync(database, user, seriesId: 10, episodeId: 103, Evening);
        await InsertEpisodeWatchAsync(database, user, seriesId: 10, episodeId: 104, Evening);
        await InsertEpisodeWatchAsync(database, user, seriesId: 10, episodeId: 105, Evening.AddMinutes(45));
        var updatedBefore = await database.ScalarAsync<DateTime>(
            "SELECT updated_utc FROM episode_watch WHERE user_id = $1 AND episode_tvdb_id = 103", user);

        await database.MigrateToAsync(WatchSource);

        (await EpisodeSourceAsync(database, user, 101)).Should().Be("Unknown");
        (await EpisodeSourceAsync(database, user, 102)).Should().Be("Bulk");
        (await EpisodeSourceAsync(database, user, 103)).Should().Be("Bulk");
        (await EpisodeSourceAsync(database, user, 104)).Should().Be("Bulk");
        (await EpisodeSourceAsync(database, user, 105)).Should().Be("Unknown");

        // The watch itself is untouched: same time, same audit stamp.
        (await database.ScalarAsync<DateTime>(
                "SELECT watched_utc FROM episode_watch WHERE user_id = $1 AND episode_tvdb_id = 103", user))
            .Should().Be(Evening);
        (await database.ScalarAsync<DateTime>(
                "SELECT updated_utc FROM episode_watch WHERE user_id = $1 AND episode_tvdb_id = 103", user))
            .Should().Be(updatedBefore);
    }

    [Test]
    public async Task WatchSource_Should_MarkAMovieTheImportWatched_AndNoOther()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        await database.MigrateToAsync(BeforeWatchSource);
        var user = await database.InsertUserAsync();
        var other = await database.InsertUserAsync();
        var job = await InsertImportJobAsync(database, user);

        // Marked watched by the import: the row was processed a moment later.
        await InsertImportItemAsync(database, job, "Heat", tvdbId: 1001, message: "Marked watched and rated 8/10.");
        await InsertMovieWatchAsync(database, user, 1001, ImportedUtc.AddMilliseconds(-15));

        // Watched weeks before the import ran ("Already watched, rating updated").
        await InsertImportItemAsync(database, job, "Ronin", tvdbId: 1002, message: "Already watched — rating updated to 7/10.");
        await InsertMovieWatchAsync(database, user, 1002, ImportedUtc.AddDays(-20));

        // Went on the watchlist by import, watched by hand later.
        await InsertImportItemAsync(database, job, "Thief", tvdbId: 1003, message: "Added to your watchlist.");
        await InsertMovieWatchAsync(database, user, 1003, ImportedUtc.AddDays(3));

        // Never imported; and the imported movie as watched by somebody else at the same moment.
        await InsertMovieWatchAsync(database, user, 1004, ImportedUtc);
        await InsertMovieWatchAsync(database, other, 1001, ImportedUtc);

        // The import row did not resolve (no match), although the times agree.
        await InsertImportItemAsync(database, job, "Collateral", tvdbId: 1005, status: "NotFound", message: "No match.");
        await InsertMovieWatchAsync(database, user, 1005, ImportedUtc);

        await database.MigrateToAsync(WatchSource);

        (await MovieSourceAsync(database, user, 1001)).Should().Be("Import");
        (await MovieSourceAsync(database, user, 1002)).Should().Be("Unknown");
        (await MovieSourceAsync(database, user, 1003)).Should().Be("Unknown");
        (await MovieSourceAsync(database, user, 1004)).Should().Be("Unknown");
        (await MovieSourceAsync(database, user, 1005)).Should().Be("Unknown");
        (await MovieSourceAsync(database, other, 1001)).Should().Be("Unknown");
    }

    [Test]
    public async Task WatchSource_Should_GiveRowsWrittenWithoutASource_TheDefault()
    {
        // What a row inserted by plain SQL (or by an old instance during a
        // deploy) gets: the column's default.
        await using var database = await MigrationDatabase.CreateAsync();
        await database.MigrateToLatestAsync();
        var user = await database.InsertUserAsync();

        await InsertEpisodeWatchAsync(database, user, seriesId: 10, episodeId: 101, Evening);
        await InsertMovieWatchAsync(database, user, 1001, Evening);

        (await EpisodeSourceAsync(database, user, 101)).Should().Be("Unknown");
        (await MovieSourceAsync(database, user, 1001)).Should().Be("Unknown");
    }

    private static Task InsertEpisodeWatchAsync(
        MigrationDatabase database, Guid user, int seriesId, int episodeId, DateTime watchedUtc) =>
        database.ExecuteAsync(
            """
            INSERT INTO episode_watch (id, user_id, series_tvdb_id, episode_tvdb_id, watched_utc, created_utc, updated_utc)
            VALUES (gen_random_uuid(), $1, $2, $3, $4, $4, $4);
            """,
            user, seriesId, episodeId, watchedUtc);

    private static Task InsertMovieWatchAsync(MigrationDatabase database, Guid user, int movieId, DateTime watchedUtc) =>
        database.ExecuteAsync(
            """
            INSERT INTO user_movie_watch (id, user_id, movie_tvdb_id, watched_utc, created_utc, updated_utc)
            VALUES (gen_random_uuid(), $1, $2, $3, $3, $3);
            """,
            user, movieId, watchedUtc);

    private static Task<string?> EpisodeSourceAsync(MigrationDatabase database, Guid user, int episodeId) =>
        database.ScalarAsync<string>(
            "SELECT source FROM episode_watch WHERE user_id = $1 AND episode_tvdb_id = $2", user, episodeId);

    private static Task<string?> MovieSourceAsync(MigrationDatabase database, Guid user, int movieId) =>
        database.ScalarAsync<string>(
            "SELECT source FROM user_movie_watch WHERE user_id = $1 AND movie_tvdb_id = $2", user, movieId);

    // ---- SeriesMappingVersion: which cached series were written with genres ----

    [Test]
    public async Task SeriesMappingVersion_Should_MarkRowsThatAlreadyHaveGenres_AndLeaveTheRestForTheRefreshJob()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        await database.MigrateToAsync(WatchSource);

        await InsertCachedSeriesAsync(database, 1, """{"tvdbId":1,"name":"Cached before genres","episodes":[]}""");
        await InsertCachedSeriesAsync(database, 2, """{"tvdbId":2,"name":"With genres","genres":["Drama","Crime"],"episodes":[]}""");
        await InsertCachedSeriesAsync(database, 3, """{"tvdbId":3,"name":"TheTVDB lists none","genres":[],"episodes":[]}""");

        await database.MigrateToAsync(SeriesMappingVersion);

        (await MappingVersionAsync(database, 1)).Should().Be(0, "it has no genres property, so it is refreshed first");
        (await MappingVersionAsync(database, 2)).Should().Be(1);
        (await MappingVersionAsync(database, 3)).Should().Be(1, "an empty list is an answer: the series has no genres");
        (await database.ScalarAsync<string>("SELECT payload ->> 'name' FROM cached_series_aggregate WHERE tvdb_id = 2"))
            .Should().Be("With genres", "the payload itself is not touched");
    }

    private static Task InsertCachedSeriesAsync(MigrationDatabase database, int tvdbId, string payload) =>
        database.ExecuteAsync(
            """
            INSERT INTO cached_series_aggregate (tvdb_id, language, name, payload, retrieved_utc)
            VALUES ($1, 'eng', 'Series', $2::jsonb, now());
            """,
            tvdbId, payload);

    private static Task<int> MappingVersionAsync(MigrationDatabase database, int tvdbId) =>
        database.ScalarAsync<int>("SELECT mapping_version FROM cached_series_aggregate WHERE tvdb_id = $1", tvdbId);

    // ---- seeding ----

    private static Task InsertNotificationAsync(
        MigrationDatabase database, Guid user, string type, int? seriesId, int? episodeId) =>
        database.ExecuteAsync(
            """
            INSERT INTO notification (id, user_id, type, title, series_tvdb_id, episode_tvdb_id, is_read, created_utc, updated_utc)
            VALUES (gen_random_uuid(), $1, $2, 'New episode', $3, $4, false, now(), now());
            """,
            user, type, seriesId, episodeId);

    private static async Task<Guid> InsertEmailAsync(
        MigrationDatabase database, string body, string? htmlBody, bool sent, int attempts)
    {
        var id = Guid.NewGuid();
        await database.ExecuteAsync(
            """
            INSERT INTO email (id, to_address, subject, body, html_body, send_attempts, sent_utc, created_utc, updated_utc)
            VALUES ($1, 'someone@example.com', 'Subject', $2, $3, $4, $5, now(), now());
            """,
            id, body, htmlBody, attempts, sent ? DateTime.UtcNow : null);
        return id;
    }

    private static async Task<(string Body, string? HtmlBody)> EmailAsync(MigrationDatabase database, Guid id) =>
        ((await database.ScalarAsync<string>("SELECT body FROM email WHERE id = $1", id))!,
            await database.ScalarAsync<string>("SELECT html_body FROM email WHERE id = $1", id));

    private static async Task<Guid> InsertImportJobAsync(MigrationDatabase database, Guid user)
    {
        var id = Guid.NewGuid();
        await database.ExecuteAsync(
            """
            INSERT INTO watchlist_import_job
                (id, user_id, file_name, status, total_count, processed_count, imported_count,
                 skipped_count, not_found_count, failed_count, created_utc, completed_utc)
            VALUES ($1, $2, 'watchlist.csv', 'Completed', 1, 1, 1, 0, 0, 0, $3, $3);
            """,
            id, user, ImportedUtc);
        return id;
    }

    private static async Task<Guid> InsertImportItemAsync(
        MigrationDatabase database,
        Guid job,
        string title,
        int? tvdbId,
        string status = "Imported",
        string message = "Added to your favorites.")
    {
        var id = Guid.NewGuid();
        await database.ExecuteAsync(
            """
            INSERT INTO watchlist_import_item
                (id, job_id, row_number, imdb_id, title, title_type, status, resolved_tvdb_id,
                 result_message, created_utc, processed_utc)
            VALUES ($1, $2, 1, 'tt0113277', $3, 'Movie', $4, $5, $6, $7, $7);
            """,
            id, job, title, status, tvdbId, message, ImportedUtc);
        return id;
    }

    private static Task InsertLikeAsync(
        MigrationDatabase database, Guid user, string targetType, int tvdbId, DateTime createdUtc) =>
        database.ExecuteAsync(
            """
            INSERT INTO user_like (id, user_id, target_type, target_tvdb_id, series_tvdb_id, created_utc, updated_utc)
            VALUES (gen_random_uuid(), $1, $2, $3, 0, $4, $4);
            """,
            user, targetType, tvdbId, createdUtc);

    private static Task<long> LikeCountAsync(MigrationDatabase database, Guid user, string targetType, int tvdbId) =>
        database.ScalarAsync<long>(
            "SELECT count(*) FROM user_like WHERE user_id = $1 AND target_type = $2 AND target_tvdb_id = $3",
            user, targetType, tvdbId);

    private static Task<string?> ResultMessageAsync(MigrationDatabase database, Guid item) =>
        database.ScalarAsync<string>("SELECT result_message FROM watchlist_import_item WHERE id = $1", item);
}
