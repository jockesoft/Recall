using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Infrastructure.Persistence.TvdbCache;

namespace Recall.Tests.Postgres.Queries;

/// <summary>
/// Queries whose SQL differs between providers: the OMDb anti-join, ordering
/// by a correlated EXISTS, GROUP BY in the database, and case folding. They are
/// covered on SQLite as well; this runs the statement PostgreSQL actually gets.
/// Several of them take "the first N rows of the table", so the tables they
/// read are emptied before each test.
/// </summary>
[TestFixture]
public sealed class PostgresQueryTests : PostgresFixture
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public Task EmptyTablesAsync() =>
        ExecuteAsync(
            """
            TRUNCATE cached_series_aggregate, cached_series_omdb, cached_movie_aggregate, cached_movie_omdb,
                     cached_episode_extended, watchlist_import_item, watchlist_import_job;
            """);

    // ---- OMDb anti-join ----

    [Test]
    public async Task SeriesNeedingOmdb_Should_ReturnMissingAndStale_OncePerSeries_InIdOrder()
    {
        await using (var db = NewContext())
        {
            db.CachedSeriesAggregates.AddRange(
                SeriesAggregate(1), SeriesAggregate(2), SeriesAggregate(3),
                SeriesAggregate(4, "eng"), SeriesAggregate(4, "swe"));
            db.CachedSeriesOmdb.AddRange(
                new CachedSeriesOmdbEntity { TvdbId = 1, RetrievedUtc = Now.AddDays(-1) },
                new CachedSeriesOmdbEntity { TvdbId = 2, RetrievedUtc = Now.AddDays(-45) });
            await db.SaveChangesAsync();
        }

        var store = new OmdbSnapshotStore(NewFactory(), NullLogger<OmdbSnapshotStore>.Instance);

        (await store.GetSeriesNeedingOmdbAsync(Now.AddDays(-30), limit: 10)).Should().Equal(2, 3, 4);
        (await store.GetSeriesNeedingOmdbAsync(Now.AddDays(-30), limit: 2)).Should().Equal(2, 3);
    }

    [Test]
    public async Task MoviesNeedingOmdb_Should_ReturnMissingAndStale_OncePerMovie_InIdOrder()
    {
        await using (var db = NewContext())
        {
            db.CachedMovieAggregates.AddRange(
                MovieAggregate(1), MovieAggregate(2), MovieAggregate(3),
                MovieAggregate(4, "eng"), MovieAggregate(4, "swe"));
            db.CachedMoviesOmdb.AddRange(
                new CachedMovieOmdbEntity { TvdbId = 1, RetrievedUtc = Now.AddDays(-1) },
                new CachedMovieOmdbEntity { TvdbId = 2, RetrievedUtc = Now.AddDays(-45) });
            await db.SaveChangesAsync();
        }

        var store = new MovieOmdbSnapshotStore(NewFactory(), NullLogger<MovieOmdbSnapshotStore>.Instance);

        (await store.GetMoviesNeedingOmdbAsync(Now.AddDays(-30), limit: 10)).Should().Equal(2, 3, 4);
        (await store.GetMoviesNeedingOmdbAsync(Now.AddDays(-30), limit: 2)).Should().Equal(2, 3);
    }

    // ---- TheTVDB refresh queues ----

    [Test]
    public async Task SeriesNeedingRefresh_Should_PutTrackedSeriesFirst_ThenOldest()
    {
        var user = await SeedUserAsync();
        await using (var db = NewContext())
        {
            db.CachedSeriesAggregates.AddRange(
                SeriesAggregate(1, retrievedUtc: Now.AddDays(-9), keepUpdated: true),
                SeriesAggregate(2, retrievedUtc: Now.AddDays(-5), keepUpdated: true),
                SeriesAggregate(3, retrievedUtc: Now.AddDays(-3), keepUpdated: true),
                SeriesAggregate(4, retrievedUtc: Now.AddHours(-1), keepUpdated: true),
                // Ended series use the longer cutoff: five days old is still fresh for them.
                SeriesAggregate(5, retrievedUtc: Now.AddDays(-5), keepUpdated: false),
                SeriesAggregate(6, retrievedUtc: Now.AddDays(-40), keepUpdated: null));
            db.TrackedSeries.Add(new TrackedSeriesEntity { Id = Guid.NewGuid(), UserId = user, TvdbId = 3, Name = "Tracked" });
            await db.SaveChangesAsync();
        }

        var keys = await TvdbStore().GetAggregatesNeedingRefreshAsync(
            staleBeforeUtc: Now.AddDays(-1), settledStaleBeforeUtc: Now.AddDays(-30), limit: 10);

        keys.Select(k => k.TvdbId).Should().Equal(3, 6, 1, 2);
    }

    [Test]
    public async Task MoviesNeedingRefresh_Should_PutMoviesSomeoneUsesFirst()
    {
        var user = await SeedUserAsync();
        await using (var db = NewContext())
        {
            db.CachedMovieAggregates.AddRange(
                MovieAggregate(1, retrievedUtc: Now.AddDays(-9)),
                MovieAggregate(2, retrievedUtc: Now.AddDays(-8)),
                MovieAggregate(3, retrievedUtc: Now.AddDays(-7)),
                MovieAggregate(4, retrievedUtc: Now.AddDays(-6)));
            db.TrackedMovies.Add(new TrackedMovieEntity { Id = Guid.NewGuid(), UserId = user, TvdbId = 2, Name = "On the watchlist" });
            db.UserMovieWatches.Add(new UserMovieWatchEntity { Id = Guid.NewGuid(), UserId = user, MovieTvdbId = 3, WatchedUtc = Now });
            db.UserLikes.Add(new UserLikeEntity { Id = Guid.NewGuid(), UserId = user, TargetType = LikeTargetType.Movie, TargetTvdbId = 4, SeriesTvdbId = 4 });
            // A liked *series* with the same id as movie 1 does not make movie 1 "used".
            db.UserLikes.Add(new UserLikeEntity { Id = Guid.NewGuid(), UserId = user, TargetType = LikeTargetType.Series, TargetTvdbId = 1, SeriesTvdbId = 1 });
            await db.SaveChangesAsync();
        }

        var keys = await TvdbStore().GetMovieAggregatesNeedingRefreshAsync(
            staleBeforeUtc: Now.AddDays(-1), settledStaleBeforeUtc: Now.AddDays(-30), limit: 10);

        keys.Select(k => k.TvdbId).Should().Equal(2, 3, 4, 1);
    }

    [Test]
    public async Task EpisodesNeedingRefresh_Should_MatchTbaInAnyCase_AndChaseMissingImages()
    {
        var today = DateOnly.FromDateTime(Now);
        await using (var db = NewContext())
        {
            db.CachedEpisodesExtended.AddRange(
                Episode(1, "Plain and fresh", Now.AddHours(-2)),
                Episode(2, "Plain and stale", Now.AddDays(-40)),
                Episode(3, "tba", Now.AddDays(-2)),
                Episode(4, "TBA", Now.AddHours(-2)),
                Episode(5, "Aired, no still", Now.AddDays(-2), aired: today.AddDays(-1), hasImage: false),
                Episode(6, "Not aired yet, no still", Now.AddDays(-2), aired: today.AddDays(1), hasImage: false),
                Episode(7, "Given up on", Now.AddDays(-2), aired: today.AddDays(-1), hasImage: false, attempts: 5),
                Episode(8, "Aired today, no still", Now.AddDays(-3), aired: today, hasImage: false));
            await db.SaveChangesAsync();
        }

        var ids = await TvdbStore().GetEpisodesNeedingRefreshAsync(
            staleBeforeUtc: Now.AddDays(-30),
            tbaStaleBeforeUtc: Now.AddDays(-1),
            imageChaseBeforeUtc: Now.AddDays(-1),
            today: today,
            maxImageChaseAttempts: 5,
            limit: 10);

        // Oldest first: 2 (stale), 8 (no still), then 3 (tba) and 5 (no still).
        ids.Should().Equal(2, 8, 3, 5);
    }

    // ---- Watchlist import queue ----

    [Test]
    public async Task ImportQueue_Should_HandOutPendingItems_OldestJobFirst_AcrossUsers()
    {
        var (firstUser, secondUser) = (await SeedUserAsync(), await SeedUserAsync());
        Guid olderJob, newerJob;

        await using (var db = NewContext())
        {
            var repository = new WatchlistImportRepository(db);
            olderJob = (await repository.CreateJobAsync(secondUser, "older.csv",
            [
                new NewWatchlistImportItem(1, "tt0000001", "One", "Movie", null, IsSupported: true),
                new NewWatchlistImportItem(2, "tt0000002", "Two", "Video Game", null, IsSupported: false),
                new NewWatchlistImportItem(3, "tt0000003", "Three", "TV Series", 8, IsSupported: true)
            ])).Id;
            newerJob = (await repository.CreateJobAsync(firstUser, "newer.csv",
            [
                new NewWatchlistImportItem(1, "tt0000004", "Four", "Movie", null, IsSupported: true)
            ])).Id;
        }

        await using var claim = NewContext();
        var batch = await new WatchlistImportRepository(claim).ClaimNextPendingBatchAsync(batchSize: 2);

        batch.Select(x => x.ImdbId).Should().Equal("tt0000001", "tt0000003");
        batch.Should().OnlyContain(x => x.JobId == olderJob && x.UserId == secondUser);

        var all = await new WatchlistImportRepository(claim).ClaimNextPendingBatchAsync(batchSize: 10);
        all.Select(x => x.JobId).Should().Equal(olderJob, olderJob, newerJob);
    }

    [Test]
    public async Task ImportProgress_Should_CountItemsByStatusInTheDatabase_AndCompleteTheJob()
    {
        var user = await SeedUserAsync();
        await using var db = NewContext();
        var repository = new WatchlistImportRepository(db);
        var job = await repository.CreateJobAsync(user, "watchlist.csv",
        [
            new NewWatchlistImportItem(1, "tt0000001", "One", "Movie", null, IsSupported: true),
            new NewWatchlistImportItem(2, "tt0000002", "Two", "Movie", null, IsSupported: true),
            new NewWatchlistImportItem(3, "tt0000003", "Three", "Movie", null, IsSupported: true),
            new NewWatchlistImportItem(4, "tt0000004", "Four", "Movie", null, IsSupported: true),
            new NewWatchlistImportItem(5, "tt0000005", "Five", "Video Game", null, IsSupported: false)
        ]);

        await repository.MarkItemResultAsync(job.Items[0].Id, WatchlistImportItemStatus.Imported, 101, "Added to your watchlist.");
        await repository.MarkItemResultAsync(job.Items[1].Id, WatchlistImportItemStatus.AlreadyInLibrary, 102, null);
        await repository.MarkItemResultAsync(job.Items[2].Id, WatchlistImportItemStatus.NotFound, null, null);
        await repository.RecalculateJobProgressAsync(job.Id);

        var inProgress = await repository.GetLatestJobForUserAsync(user, includeItems: false);
        inProgress!.Status.Should().Be(WatchlistImportJobStatus.Processing);
        inProgress.ProcessedCount.Should().Be(4);
        inProgress.ImportedCount.Should().Be(1);
        inProgress.SkippedCount.Should().Be(2, "one already in the library and one unsupported row");
        inProgress.NotFoundCount.Should().Be(1);
        (await repository.GetActiveJobForUserAsync(user)).Should().NotBeNull();

        await repository.MarkItemResultAsync(job.Items[3].Id, WatchlistImportItemStatus.Failed, null, "TheTVDB did not answer.");
        await repository.RecalculateJobProgressAsync(job.Id);

        var done = await repository.GetLatestJobForUserAsync(user, includeItems: false);
        done!.Status.Should().Be(WatchlistImportJobStatus.Completed);
        done.FailedCount.Should().Be(1);
        done.CompletedUtc.Should().NotBeNull();
        (await repository.GetActiveJobForUserAsync(user)).Should().BeNull();
    }

    // ---- Aggregates and case folding ----

    [Test]
    public async Task RatingSummary_Should_AverageInTheDatabase()
    {
        var seriesId = NextId();
        foreach (var value in new[] { 7, 8, 10 })
        {
            var user = await SeedUserAsync();
            await using var db = NewContext();
            await new RatingRepository(db, NullLogger<RatingRepository>.Instance)
                .RateAsync(user, RatingTargetType.Series, seriesId, seriesId, value);
        }

        await using var read = NewContext();
        var repository = new RatingRepository(read, NullLogger<RatingRepository>.Instance);

        var summary = await repository.GetSummaryAsync(RatingTargetType.Series, seriesId);
        summary.Count.Should().Be(3);
        summary.Average.Should().BeApproximately(8.3333, 0.0001);

        (await repository.GetSummaryAsync(RatingTargetType.Series, NextId())).Should().Be(RatingSummary.Empty);
        (await repository.GetSummaryAsync(RatingTargetType.Movie, seriesId)).Should().Be(RatingSummary.Empty);
    }

    [Test]
    public async Task Username_Should_BeTaken_RegardlessOfCase()
    {
        var name = $"Saga-{Guid.NewGuid():N}";
        var owner = await SeedUserAsync(name);
        var other = await SeedUserAsync();

        await using var db = NewContext();
        var repository = new AppUserRepository(db);

        (await repository.IsUsernameAvailableAsync(name.ToUpperInvariant(), other)).Should().BeFalse();
        (await repository.IsUsernameAvailableAsync(name.ToUpperInvariant(), owner)).Should().BeTrue();
        (await repository.UpdateUsernameAsync(other, name.ToLowerInvariant())).Should().Be(UsernameUpdateResult.Taken);
    }

    private TvdbSnapshotStore TvdbStore() => new(NewFactory(), NullLogger<TvdbSnapshotStore>.Instance);

    private static CachedSeriesAggregateEntity SeriesAggregate(
        int id, string language = "eng", DateTime? retrievedUtc = null, bool? keepUpdated = null) =>
        new()
        {
            TvdbId = id, Language = language, Name = $"Series {id}", Payload = "{}",
            KeepUpdated = keepUpdated, RetrievedUtc = retrievedUtc ?? Now
        };

    private static CachedMovieAggregateEntity MovieAggregate(
        int id, string language = "eng", DateTime? retrievedUtc = null) =>
        new()
        {
            TvdbId = id, Language = language, Name = $"Movie {id}", Payload = "{}",
            KeepUpdated = true, RetrievedUtc = retrievedUtc ?? Now
        };

    private static CachedEpisodeExtendedEntity Episode(
        int id, string name, DateTime retrievedUtc, DateOnly? aired = null, bool hasImage = true, int attempts = 0) =>
        new()
        {
            EpisodeTvdbId = id, Name = name, Payload = "{}", RetrievedUtc = retrievedUtc,
            Aired = aired, HasImage = hasImage, RefreshAttempts = attempts
        };

    // ---- weekly digest ----

    [Test]
    public async Task DigestRecipients_Should_BeOptedInUsersWithoutALedgerRowForTheWeek()
    {
        await ExecuteAsync("TRUNCATE app_user CASCADE;");
        var week = new DateOnly(2026, 10, 2);
        var due = await SeedUserAsync("due");
        var done = await SeedUserAsync("done");
        var doneLastWeek = await SeedUserAsync("done-last-week");
        var notOptedIn = await SeedUserAsync("not-opted-in");

        await using (var db = NewContext())
        {
            var users = new AppUserRepository(db);
            (await users.SetDigestOptInAsync(due, true)).Should().BeTrue();
            (await users.SetDigestOptInAsync(done, true)).Should().BeTrue();
            (await users.SetDigestOptInAsync(doneLastWeek, true)).Should().BeTrue();

            var digests = new DigestRepository(db);
            await digests.RecordAsync(done, week, DigestSendStatus.Skipped, null);
            await digests.RecordAsync(doneLastWeek, week.AddDays(-7), DigestSendStatus.Skipped, null);
        }

        await using var read = NewContext();
        var recipients = await new DigestRepository(read).GetDueRecipientsAsync(week, 10);

        recipients.Select(r => r.UserId).Should().BeEquivalentTo([due, doneLastWeek]);
        recipients.Select(r => r.UserId).Should().NotContain(notOptedIn);
    }

    [Test]
    public async Task TheDigestPreference_Should_KeepTheFirstOptInTime_AndTheDismissalShouldBeSetOnce()
    {
        var user = await SeedUserAsync();

        await using (var db = NewContext())
        {
            var users = new AppUserRepository(db);
            await users.SetDigestOptInAsync(user, true);
            await users.DismissDigestPromptAsync(user);
        }

        DateTime? firstOptIn, firstDismissal;
        await using (var read = NewContext())
        {
            var row = await read.AppUsers.AsNoTracking().SingleAsync(x => x.Id == user);
            (firstOptIn, firstDismissal) = (row.DigestOptedInUtc, row.DigestPromptDismissedUtc);
            firstOptIn.Should().NotBeNull();
            firstDismissal.Should().NotBeNull();
        }

        await using (var db = NewContext())
        {
            var users = new AppUserRepository(db);
            await users.SetDigestOptInAsync(user, true);     // already on
            await users.DismissDigestPromptAsync(user);      // already dismissed
        }

        await using (var read = NewContext())
        {
            var row = await read.AppUsers.AsNoTracking().SingleAsync(x => x.Id == user);
            row.DigestOptedInUtc.Should().Be(firstOptIn, "the time of the consent is not overwritten");
            row.DigestPromptDismissedUtc.Should().Be(firstDismissal);
        }

        await using (var db = NewContext())
        {
            var users = new AppUserRepository(db);
            (await users.SetDigestOptInAsync(user, false)).Should().BeTrue();
            (await users.SetDigestOptInAsync(Guid.NewGuid(), false)).Should().BeFalse("no such user");
        }

        await using var after = NewContext();
        (await after.AppUsers.AsNoTracking().SingleAsync(x => x.Id == user)).DigestOptedInUtc.Should().BeNull();
    }

    [Test]
    public async Task ADigestEmail_Should_FitInTheQueue_HoweverLongItsTextPartIs()
    {
        // The body column used to be limited to 2,000 characters; a digest's text part is longer.
        var user = await SeedUserAsync();
        var address = $"{user:N}@example.com";
        var longText = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"- Series {i}: S01 · E{i:D2} https://recall.example/Episodes/Details/{i}"));

        await using (var db = NewContext())
        {
            (await new DigestRepository(db).RecordAsync(user, new DateOnly(2026, 10, 2), DigestSendStatus.Queued, new Recall.Web.Domain.Internal.OutboundEmail
            {
                Id = Guid.NewGuid(), ToAddress = address, Subject = "Your week on Recall", Body = longText, HtmlBody = "<p>html</p>"
            })).Should().BeTrue();
        }

        await using var read = NewContext();
        (await read.Emails.AsNoTracking().SingleAsync(x => x.ToAddress == address)).Body.Length.Should().BeGreaterThan(2000);
    }
}
