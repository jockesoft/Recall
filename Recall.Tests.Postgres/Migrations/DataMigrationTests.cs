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
