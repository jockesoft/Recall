using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using AwesomeAssertions;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.TvdbCache;

namespace Recall.Tests.Infrastructure.Persistence;

[TestFixture]
public sealed class TvdbSnapshotStoreTests
{
    // Rows the current mapping wrote; a seeded row without it counts as cached before genres.
    private const int Current = Recall.Web.Mappings.SeriesDataDtoMappings.AggregateVersion;

    private SqliteConnection _connection = null!;
    private DbContextOptions<AppDbContext> _dbOptions = null!;

    [SetUp]
    public async Task SetUpAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using var db = new AppDbContext(_dbOptions);
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public async Task TearDownAsync() => await _connection.DisposeAsync();

    private TvdbSnapshotStore NewStore() =>
        new(new PooledLikeFactory(_dbOptions), NullLogger<TvdbSnapshotStore>.Instance);

    // Minimal IDbContextFactory over the shared in-memory SQLite connection —
    // every CreateDbContext() call hands back a context bound to the same DB.
    private sealed class PooledLikeFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    [Test]
    public async Task SaveThenGet_SeriesAggregate_RoundTripsTheGraph()
    {
        var aggregate = new SeriesAggregate
        {
            TvdbId = 100,
            Name = "Round Trip Show",
            Slug = "round-trip",
            Status = new SeriesStatus { Name = "Ended", KeepUpdated = false },
            Episodes =
            [
                new EpisodeSummary { Id = 1, SeasonNumber = 1, EpisodeNumber = 1, Name = "Pilot", Aired = new DateOnly(2020, 1, 1) }
            ]
        };

        await NewStore().SaveSeriesAggregateAsync(aggregate, "eng");

        var loaded = await NewStore().GetSeriesAggregateAsync(100, "eng");

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Round Trip Show");
        loaded.Status!.Name.Should().Be("Ended");
        loaded.Episodes.Should().ContainSingle();
        loaded.Episodes[0].Name.Should().Be("Pilot");
        loaded.Episodes[0].Aired.Should().Be(new DateOnly(2020, 1, 1));
    }

    [Test]
    public async Task SaveSeriesAggregate_IsInsertOnly_DoesNotOverwriteExisting()
    {
        await NewStore().SaveSeriesAggregateAsync(new SeriesAggregate { TvdbId = 200, Name = "Original" }, "eng");
        await NewStore().SaveSeriesAggregateAsync(new SeriesAggregate { TvdbId = 200, Name = "Replacement" }, "eng");

        var loaded = await NewStore().GetSeriesAggregateAsync(200, "eng");

        loaded!.Name.Should().Be("Original");
    }

    [Test]
    public async Task GetSeriesAggregate_ReturnsNull_WhenMissing()
    {
        (await NewStore().GetSeriesAggregateAsync(999, "eng")).Should().BeNull();
    }

    [Test]
    public async Task GetSeriesAggregate_ReturnsNull_WhenPayloadIsCorrupt()
    {
        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.CachedSeriesAggregates.Add(new CachedSeriesAggregateEntity
            {
                TvdbId = 300,
                Language = "eng",
                Name = "Corrupt",
                Payload = "{ this is not json",
                RetrievedUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        (await NewStore().GetSeriesAggregateAsync(300, "eng")).Should().BeNull();
    }

    [Test]
    public async Task SaveThenGet_MovieAggregate_RoundTripsTheGraph()
    {
        var aggregate = new MovieAggregate
        {
            TvdbId = 287533,
            Name = "Oppenheimer",
            Status = new MovieStatus { Name = "Released", KeepUpdated = true },
            Genres = ["Drama", "History"],
            Characters = [new Character { Id = 1, Name = "J. Robert Oppenheimer", PersonName = "Cillian Murphy" }]
        };

        await NewStore().SaveMovieAggregateAsync(aggregate, "eng");

        var loaded = await NewStore().GetMovieAggregateAsync(287533, "eng");

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Oppenheimer");
        loaded.Status!.Name.Should().Be("Released");
        loaded.Genres.Should().BeEquivalentTo(["Drama", "History"]);
        loaded.Characters.Should().ContainSingle().Which.PersonName.Should().Be("Cillian Murphy");
    }

    [Test]
    public async Task SaveMovieAggregate_IsInsertOnly_DoesNotOverwriteExisting()
    {
        await NewStore().SaveMovieAggregateAsync(new MovieAggregate { TvdbId = 200, Name = "Original" }, "eng");
        await NewStore().SaveMovieAggregateAsync(new MovieAggregate { TvdbId = 200, Name = "Replacement" }, "eng");

        var loaded = await NewStore().GetMovieAggregateAsync(200, "eng");

        loaded!.Name.Should().Be("Original");
    }

    [Test]
    public async Task GetMovieAggregate_ReturnsNull_WhenMissing()
    {
        (await NewStore().GetMovieAggregateAsync(999, "eng")).Should().BeNull();
    }

    [Test]
    public async Task GetMovieAggregate_ReturnsNull_WhenPayloadIsCorrupt()
    {
        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.CachedMovieAggregates.Add(new CachedMovieAggregateEntity
            {
                TvdbId = 300,
                Language = "eng",
                Name = "Corrupt",
                Payload = "{ this is not json",
                RetrievedUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        (await NewStore().GetMovieAggregateAsync(300, "eng")).Should().BeNull();
    }

    [Test]
    public async Task UpsertMovieAggregate_OverwritesExisting()
    {
        await NewStore().SaveMovieAggregateAsync(new MovieAggregate { TvdbId = 400, Name = "Working Title" }, "eng");
        await NewStore().UpsertMovieAggregateAsync(new MovieAggregate { TvdbId = 400, Name = "Final Title" }, "eng");

        var loaded = await NewStore().GetMovieAggregateAsync(400, "eng");

        loaded!.Name.Should().Be("Final Title");
    }

    [Test]
    public async Task GetMovieAggregatesNeedingRefresh_PicksStaleKeepUpdated_ButNotFreshOrUnflagged()
    {
        var now = DateTime.UtcNow;

        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.CachedMovieAggregates.AddRange(
                new CachedMovieAggregateEntity { TvdbId = 1, Language = "eng", Name = "Stale, keep-updated", Payload = "{}", KeepUpdated = true, RetrievedUtc = now.AddHours(-13) },
                new CachedMovieAggregateEntity { TvdbId = 2, Language = "eng", Name = "Fresh, keep-updated", Payload = "{}", KeepUpdated = true, RetrievedUtc = now.AddHours(-1) },
                new CachedMovieAggregateEntity { TvdbId = 3, Language = "eng", Name = "Stale, not keep-updated", Payload = "{}", KeepUpdated = false, RetrievedUtc = now.AddHours(-13) });
            await seed.SaveChangesAsync();
        }

        var due = await NewStore().GetMovieAggregatesNeedingRefreshAsync(
            staleBeforeUtc: now.AddHours(-12), settledStaleBeforeUtc: now.AddDays(-30), limit: 10);

        due.Should().ContainSingle().Which.TvdbId.Should().Be(1);
    }

    [Test]
    public async Task GetMovieAggregatesNeedingRefresh_AlsoPicksUnflaggedRows_OnceOlderThanTheSettledAge()
    {
        var now = DateTime.UtcNow;

        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.CachedMovieAggregates.AddRange(
                new CachedMovieAggregateEntity { TvdbId = 1, Language = "eng", Name = "Released long ago, never refreshed", Payload = "{}", KeepUpdated = false, RetrievedUtc = now.AddDays(-31) },
                new CachedMovieAggregateEntity { TvdbId = 2, Language = "eng", Name = "Flag unknown, never refreshed", Payload = "{}", KeepUpdated = null, RetrievedUtc = now.AddDays(-45) },
                new CachedMovieAggregateEntity { TvdbId = 3, Language = "eng", Name = "Released, refreshed last week", Payload = "{}", KeepUpdated = false, RetrievedUtc = now.AddDays(-7) });
            await seed.SaveChangesAsync();
        }

        var due = await NewStore().GetMovieAggregatesNeedingRefreshAsync(
            staleBeforeUtc: now.AddHours(-12), settledStaleBeforeUtc: now.AddDays(-30), limit: 10);

        due.Select(x => x.TvdbId).Should().Equal(2, 1);
    }

    [Test]
    public async Task GetMovieAggregatesNeedingRefresh_PutsMoviesAUserHasWatchlistedWatchedOrLikedFirst()
    {
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();

        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.AppUsers.Add(new AppUserEntity { Id = userId, Username = "u", Email = "u@test.local" });
            seed.CachedMovieAggregates.AddRange(
                new CachedMovieAggregateEntity { TvdbId = 1, Language = "eng", Name = "Oldest, nobody's", Payload = "{}", KeepUpdated = true, RetrievedUtc = now.AddDays(-9) },
                new CachedMovieAggregateEntity { TvdbId = 2, Language = "eng", Name = "Watched", Payload = "{}", KeepUpdated = true, RetrievedUtc = now.AddDays(-2) },
                new CachedMovieAggregateEntity { TvdbId = 3, Language = "eng", Name = "Liked", Payload = "{}", KeepUpdated = true, RetrievedUtc = now.AddDays(-3) },
                new CachedMovieAggregateEntity { TvdbId = 4, Language = "eng", Name = "On a watchlist", Payload = "{}", KeepUpdated = true, RetrievedUtc = now.AddDays(-1) });
            seed.TrackedMovies.Add(new TrackedMovieEntity { Id = Guid.NewGuid(), UserId = userId, TvdbId = 4, Name = "On a watchlist" });
            seed.UserMovieWatches.Add(new UserMovieWatchEntity { Id = Guid.NewGuid(), UserId = userId, MovieTvdbId = 2, WatchedUtc = now });
            seed.UserLikes.Add(new UserLikeEntity { Id = Guid.NewGuid(), UserId = userId, TargetType = LikeTargetType.Movie, TargetTvdbId = 3, SeriesTvdbId = 3 });
            // A like on a *series* that happens to share id 1 must not promote movie 1.
            seed.UserLikes.Add(new UserLikeEntity { Id = Guid.NewGuid(), UserId = userId, TargetType = LikeTargetType.Series, TargetTvdbId = 1, SeriesTvdbId = 1 });
            await seed.SaveChangesAsync();
        }

        var due = await NewStore().GetMovieAggregatesNeedingRefreshAsync(
            staleBeforeUtc: now.AddHours(-12), settledStaleBeforeUtc: now.AddDays(-30), limit: 10);

        due.Select(x => x.TvdbId).Should().Equal(3, 2, 4, 1);
    }

    [Test]
    public async Task GetAggregatesNeedingRefresh_PicksBothTiers_ButNotRowsStillFreshForTheirTier()
    {
        var now = DateTime.UtcNow;

        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.CachedSeriesAggregates.AddRange(
                new CachedSeriesAggregateEntity { TvdbId = 1, Language = "eng", Name = "Continuing, stale", Payload = "{}", MappingVersion = Current, KeepUpdated = true, RetrievedUtc = now.AddHours(-13) },
                new CachedSeriesAggregateEntity { TvdbId = 2, Language = "eng", Name = "Continuing, fresh", Payload = "{}", MappingVersion = Current, KeepUpdated = true, RetrievedUtc = now.AddHours(-1) },
                new CachedSeriesAggregateEntity { TvdbId = 3, Language = "eng", Name = "Ended, cached two weeks ago", Payload = "{}", MappingVersion = Current, KeepUpdated = false, RetrievedUtc = now.AddDays(-14) },
                new CachedSeriesAggregateEntity { TvdbId = 4, Language = "eng", Name = "Ended, never refreshed", Payload = "{}", MappingVersion = Current, KeepUpdated = false, RetrievedUtc = now.AddDays(-40) },
                new CachedSeriesAggregateEntity { TvdbId = 5, Language = "eng", Name = "Flag unknown, never refreshed", Payload = "{}", MappingVersion = Current, KeepUpdated = null, RetrievedUtc = now.AddDays(-60) });
            await seed.SaveChangesAsync();
        }

        var due = await NewStore().GetAggregatesNeedingRefreshAsync(
            staleBeforeUtc: now.AddHours(-12), settledStaleBeforeUtc: now.AddDays(-30), limit: 10);

        due.Select(x => x.TvdbId).Should().Equal(5, 4, 1);
    }

    [Test]
    public async Task GetAggregatesNeedingRefresh_PutsTrackedSeriesFirst_ThenOldest_WithinTheCap()
    {
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();

        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.AppUsers.Add(new AppUserEntity { Id = userId, Username = "u", Email = "u@test.local" });
            seed.CachedSeriesAggregates.AddRange(
                new CachedSeriesAggregateEntity { TvdbId = 1, Language = "eng", Name = "Untracked, oldest", Payload = "{}", MappingVersion = Current, KeepUpdated = true, RetrievedUtc = now.AddDays(-9) },
                new CachedSeriesAggregateEntity { TvdbId = 2, Language = "eng", Name = "Untracked", Payload = "{}", MappingVersion = Current, KeepUpdated = true, RetrievedUtc = now.AddDays(-8) },
                new CachedSeriesAggregateEntity { TvdbId = 3, Language = "eng", Name = "Tracked, newer", Payload = "{}", MappingVersion = Current, KeepUpdated = true, RetrievedUtc = now.AddDays(-1) },
                new CachedSeriesAggregateEntity { TvdbId = 4, Language = "eng", Name = "Tracked, older", Payload = "{}", MappingVersion = Current, KeepUpdated = true, RetrievedUtc = now.AddDays(-2) });
            await seed.SaveChangesAsync();

            // Raw SQL: tracked_series.xmin is a Postgres system column that EF
            // never writes, but on SQLite it is an ordinary NOT NULL column, so
            // an EF insert of TrackedSeriesEntity fails here.
            foreach (var tvdbId in new[] { 3, 4 })
            {
                await seed.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO tracked_series (id, user_id, tvdb_id, name, created_utc, updated_utc, xmin)
                     VALUES ({Guid.NewGuid()}, {userId}, {tvdbId}, {"Tracked"}, {now}, {now}, 1)
                     """);
            }
        }

        var due = await NewStore().GetAggregatesNeedingRefreshAsync(
            staleBeforeUtc: now.AddHours(-12), settledStaleBeforeUtc: now.AddDays(-30), limit: 3);

        due.Select(x => x.TvdbId).Should().Equal(4, 3, 1);
    }

    [Test]
    public async Task GetAggregatesNeedingRefresh_PutsRowsWithoutGenresFirst_WhateverTheirAge_WithinTheCap()
    {
        // Version 0 is a row cached before series carried TheTVDB's genres.
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();

        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.AppUsers.Add(new AppUserEntity { Id = userId, Username = "u", Email = "u@test.local" });
            seed.CachedSeriesAggregates.AddRange(
                new CachedSeriesAggregateEntity { TvdbId = 1, Language = "eng", Name = "Has genres, very stale, tracked", Payload = "{}", MappingVersion = Current, KeepUpdated = true, RetrievedUtc = now.AddDays(-90) },
                new CachedSeriesAggregateEntity { TvdbId = 2, Language = "eng", Name = "No genres, cached an hour ago", Payload = "{}", MappingVersion = 0, KeepUpdated = true, RetrievedUtc = now.AddHours(-1) },
                new CachedSeriesAggregateEntity { TvdbId = 3, Language = "eng", Name = "No genres, ended, a week old", Payload = "{}", MappingVersion = 0, KeepUpdated = false, RetrievedUtc = now.AddDays(-7) },
                new CachedSeriesAggregateEntity { TvdbId = 4, Language = "eng", Name = "No genres, tracked, newest", Payload = "{}", MappingVersion = 0, KeepUpdated = false, RetrievedUtc = now.AddMinutes(-5) },
                new CachedSeriesAggregateEntity { TvdbId = 5, Language = "eng", Name = "Has genres, fresh", Payload = "{}", MappingVersion = Current, KeepUpdated = true, RetrievedUtc = now.AddHours(-1) });
            await seed.SaveChangesAsync();

            foreach (var tvdbId in new[] { 1, 4 })
            {
                await seed.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO tracked_series (id, user_id, tvdb_id, name, created_utc, updated_utc, xmin)
                     VALUES ({Guid.NewGuid()}, {userId}, {tvdbId}, {"Tracked"}, {now}, {now}, 1)
                     """);
            }
        }

        var all = await NewStore().GetAggregatesNeedingRefreshAsync(
            staleBeforeUtc: now.AddHours(-12), settledStaleBeforeUtc: now.AddDays(-30), limit: 10);
        all.Select(x => x.TvdbId).Should().Equal(
            [4, 3, 2, 1],
            "rows without genres first (tracked, then oldest), then the age-based queue; the fresh row with genres is not due");

        var capped = await NewStore().GetAggregatesNeedingRefreshAsync(
            staleBeforeUtc: now.AddHours(-12), settledStaleBeforeUtc: now.AddDays(-30), limit: 2);
        capped.Select(x => x.TvdbId).Should().Equal([4, 3], "the cap is the same one; the backfill only goes first");
    }

    [Test]
    public async Task GetAggregatesNeedingRefresh_FallsBackToAge_OnceEveryRowHasGenres()
    {
        var now = DateTime.UtcNow;
        var store = NewStore();

        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.CachedSeriesAggregates.AddRange(
                new CachedSeriesAggregateEntity { TvdbId = 1, Language = "eng", Name = "Stale", Payload = "{}", MappingVersion = Current, KeepUpdated = true, RetrievedUtc = now.AddDays(-3) },
                new CachedSeriesAggregateEntity { TvdbId = 2, Language = "eng", Name = "No genres yet", Payload = "{}", MappingVersion = 0, KeepUpdated = true, RetrievedUtc = now.AddHours(-1) },
                new CachedSeriesAggregateEntity { TvdbId = 3, Language = "eng", Name = "Staler", Payload = "{}", MappingVersion = Current, KeepUpdated = true, RetrievedUtc = now.AddDays(-5) });
            await seed.SaveChangesAsync();
        }

        (await store.GetAggregatesNeedingRefreshAsync(now.AddHours(-12), now.AddDays(-30), limit: 10))
            .Select(x => x.TvdbId).Should().Equal(2, 3, 1);

        // What the job does with series 2: the refresh rewrites the row with the current mapping.
        await store.UpsertSeriesAggregateAsync(new SeriesAggregate { TvdbId = 2, Name = "Now with genres", Genres = ["Drama"] }, "eng");

        (await store.GetAggregatesNeedingRefreshAsync(now.AddHours(-12), now.AddDays(-30), limit: 10))
            .Select(x => x.TvdbId).Should().Equal([3, 1], "nothing is left to backfill, so the queue is oldest first, as before");
    }

    [Test]
    public async Task ASeriesWithNoGenresOnTheTvDb_Should_NotBeRefreshedForEver()
    {
        // The priority is for rows the current mapping has not written, not
        // for series that have no genres: once refreshed, such a row is done.
        var now = DateTime.UtcNow;
        var store = NewStore();
        await store.SaveSeriesAggregateAsync(new SeriesAggregate { TvdbId = 1, Name = "Genreless", Genres = [] }, "eng");

        (await store.GetAggregatesNeedingRefreshAsync(now.AddHours(-12), now.AddDays(-30), limit: 10)).Should().BeEmpty();

        await using var read = new AppDbContext(_dbOptions);
        (await read.CachedSeriesAggregates.SingleAsync()).MappingVersion.Should().Be(Current);
    }

    [Test]
    public async Task SaveThenGet_SeriesExtended_And_EpisodeExtended_RoundTrip()
    {
        var store = NewStore();
        await store.SaveSeriesExtendedAsync(new Series { Id = 400, Name = "Extended", Slug = "ext" });
        await store.SaveEpisodeExtendedAsync(new Episode { Id = 4001, SeriesId = 400, Name = "Ep One" });

        var readStore = NewStore();

        var series = await readStore.GetSeriesExtendedAsync(400);
        series!.Name.Should().Be("Extended");
        series.Slug.Should().Be("ext");

        var episode = await readStore.GetEpisodeExtendedAsync(4001);
        episode!.Name.Should().Be("Ep One");
        episode.SeriesId.Should().Be(400);
    }

    [Test]
    public async Task SaveEpisodeExtended_WithNullId_IsNoOp()
    {
        await NewStore().SaveEpisodeExtendedAsync(new Episode { Id = null, Name = "No Id" });

        await using var read = new AppDbContext(_dbOptions);
        (await read.CachedEpisodesExtended.CountAsync()).Should().Be(0);
    }

    [Test]
    public async Task UpsertEpisodeExtended_OverwritesExisting()
    {
        await NewStore().SaveEpisodeExtendedAsync(new Episode { Id = 5001, SeriesId = 500, Name = "TBA" });
        await NewStore().UpsertEpisodeExtendedAsync(new Episode { Id = 5001, SeriesId = 500, Name = "The Real Title" });

        var loaded = await NewStore().GetEpisodeExtendedAsync(5001);

        loaded!.Name.Should().Be("The Real Title");
    }

    [Test]
    public async Task GetEpisodesNeedingRefresh_PicksStaleAndTba_ButNotFresh()
    {
        var now = DateTime.UtcNow;

        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.CachedEpisodesExtended.AddRange(
                new CachedEpisodeExtendedEntity { EpisodeTvdbId = 1, Name = "Old", Payload = "{}", RetrievedUtc = now.AddDays(-40) },
                new CachedEpisodeExtendedEntity { EpisodeTvdbId = 2, Name = "tba", Payload = "{}", RetrievedUtc = now.AddHours(-13) },
                new CachedEpisodeExtendedEntity { EpisodeTvdbId = 3, Name = "TBA", Payload = "{}", RetrievedUtc = now.AddHours(-3) },
                new CachedEpisodeExtendedEntity { EpisodeTvdbId = 4, Name = "Fresh", Payload = "{}", RetrievedUtc = now.AddDays(-2) });
            await seed.SaveChangesAsync();
        }

        var due = await NewStore().GetEpisodesNeedingRefreshAsync(
            staleBeforeUtc: now.AddDays(-30),
            tbaStaleBeforeUtc: now.AddHours(-12),
            stillRecheck: new StillRecheck(now),
            limit: 10);

        // 1: older than 30d. 2: still "TBA" and older than 12h.
        // 3: "TBA" but only 3h old. 4: fresh and titled.
        due.Should().BeEquivalentTo(new[] { 1, 2 });
    }

    private static CachedEpisodeExtendedEntity NoStill(int id, DateOnly aired, DateTime retrievedUtc, bool hasImage = false) =>
        new() { EpisodeTvdbId = id, Name = $"Episode {id}", Payload = "{}", Aired = aired, HasImage = hasImage, RetrievedUtc = retrievedUtc };

    [Test]
    public async Task GetEpisodesNeedingRefresh_RechecksAnAiredEpisodeWithoutAStill_DailyForThirtyDays_WeeklyToNinety_ThenNever()
    {
        var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        var today = DateOnly.FromDateTime(now);

        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.CachedEpisodesExtended.AddRange(
                // Daily while it aired within 30 days.
                NoStill(10, today, now.AddHours(-25)),                    // aired today, checked yesterday: due
                NoStill(11, today.AddDays(-3), now.AddHours(-23)),        // checked less than a day ago: not yet
                NoStill(12, today.AddDays(-30), now.AddDays(-2)),         // the 30th day is still daily: due
                // Weekly from day 31 to day 90.
                NoStill(20, today.AddDays(-31), now.AddDays(-2)),         // checked two days ago: not yet
                NoStill(21, today.AddDays(-31), now.AddDays(-8)),         // checked over a week ago: due
                NoStill(22, today.AddDays(-90), now.AddDays(-8)),         // the 90th day is still weekly: due
                // Then never.
                NoStill(30, today.AddDays(-91), now.AddDays(-20)),        // 91 days: no more still rechecks
                NoStill(31, today.AddDays(-400), now.AddDays(-29)),
                // Never for these, whatever their age.
                NoStill(40, today.AddDays(1), now.AddDays(-5)),           // not aired yet
                NoStill(41, today.AddDays(-3), now.AddDays(-5), hasImage: true),
                new CachedEpisodeExtendedEntity { EpisodeTvdbId = 42, Name = "No air date", Payload = "{}", HasImage = false, RetrievedUtc = now.AddDays(-5) });
            await seed.SaveChangesAsync();
        }

        var due = await NewStore().GetEpisodesNeedingRefreshAsync(
            staleBeforeUtc: now.AddDays(-30),
            tbaStaleBeforeUtc: now.AddHours(-12),
            stillRecheck: new StillRecheck(now),
            limit: 50);

        due.Should().BeEquivalentTo([10, 12, 21, 22]);
    }

    [Test]
    public async Task GetEpisodesNeedingRefresh_RechecksForAStill_HoweverOftenTheRowWasRefreshedBeforeItAired()
    {
        // The old rule counted consecutive refreshes without an image and gave
        // up after five, including refreshes from before the episode aired (a
        // "TBA" title is refreshed twice a day). The schedule goes by the air date.
        var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        var store = NewStore();
        await store.SaveEpisodeExtendedAsync(new Episode { Id = 6001, Name = "Ep", Aired = "2026-10-01", Image = null });
        for (var i = 0; i < 8; i++)
            await store.UpsertEpisodeExtendedAsync(new Episode { Id = 6001, Name = "Ep", Aired = "2026-10-01", Image = null });

        await using (var db = new AppDbContext(_dbOptions))
        {
            var row = await db.CachedEpisodesExtended.SingleAsync(x => x.EpisodeTvdbId == 6001);
            row.RetrievedUtc = now.AddDays(-2);
            await db.SaveChangesAsync();
        }

        (await store.GetEpisodesNeedingRefreshAsync(now.AddDays(-30), now.AddHours(-12), new StillRecheck(now), limit: 10))
            .Should().Equal(6001);
    }

    [Test]
    public async Task GetEpisodesNeedingRefresh_KeepsStillRechecksWithinTheCap_OldestCheckedFirst()
    {
        var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        var today = DateOnly.FromDateTime(now);

        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.CachedEpisodesExtended.AddRange(
                Enumerable.Range(1, 40).Select(n => NoStill(n, today.AddDays(-2), now.AddDays(-2).AddMinutes(-n))));
            await seed.SaveChangesAsync();
        }

        var due = await NewStore().GetEpisodesNeedingRefreshAsync(
            now.AddDays(-30), now.AddHours(-12), new StillRecheck(now), limit: 25);

        due.Should().HaveCount(25, "the still rechecks share the episode tier's cap; they add no budget of their own");
        due.Should().Equal(Enumerable.Range(16, 25).Reverse(), "the rows checked longest ago go first; the rest wait for the next run");
    }

    // ---- series that rarely have stills -----------------------------------------

    private static CachedSeriesAggregateEntity SeriesWithStills(int id, int aired, int withStill) =>
        new()
        {
            TvdbId = id, Language = "eng", Name = $"Series {id}", Payload = "{}", MappingVersion = Current,
            KeepUpdated = true, RetrievedUtc = DateTime.UtcNow, AiredEpisodeCount = aired, AiredStillCount = withStill
        };

    private static CachedEpisodeExtendedEntity DueForAStill(int id, int seriesId, DateTime now) =>
        new()
        {
            EpisodeTvdbId = id, SeriesTvdbId = seriesId, Name = $"Episode {id}", Payload = "{}",
            Aired = DateOnly.FromDateTime(now).AddDays(-2), HasImage = false, RetrievedUtc = now.AddDays(-2)
        };

    [Test]
    public async Task GetEpisodesNeedingRefresh_SkipsTheEpisodesOfASeriesThatRarelyHasStills()
    {
        var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.CachedSeriesAggregates.AddRange(
                SeriesWithStills(1, aired: 177, withStill: 2),      // 1%: below the threshold
                SeriesWithStills(2, aired: 330, withStill: 306),    // 93%: above
                SeriesWithStills(3, aired: 9, withStill: 0),        // none, but fewer than 10 aired: too early to judge
                SeriesWithStills(4, aired: 100, withStill: 10),     // exactly 10% is not "fewer than 10%"
                SeriesWithStills(5, aired: 100, withStill: 9),      // 9%: below
                SeriesWithStills(6, aired: 10, withStill: 0));      // the tenth aired episode makes it judged
            seed.CachedEpisodesExtended.AddRange(
                DueForAStill(101, seriesId: 1, now),
                DueForAStill(102, seriesId: 2, now),
                DueForAStill(103, seriesId: 3, now),
                DueForAStill(104, seriesId: 4, now),
                DueForAStill(105, seriesId: 5, now),
                DueForAStill(106, seriesId: 6, now),
                DueForAStill(107, seriesId: 999, now));             // its series is not cached: rechecked as usual
            await seed.SaveChangesAsync();
        }

        var due = await NewStore().GetEpisodesNeedingRefreshAsync(
            now.AddDays(-30), now.AddHours(-12), new StillRecheck(now) { MinStillPercent = 10, MinAiredEpisodes = 10 }, limit: 50);

        due.Should().BeEquivalentTo([102, 103, 104, 107]);
    }

    [Test]
    public async Task GetEpisodesNeedingRefresh_UsesTheConfiguredThresholds_AndZeroPercentTurnsTheRuleOff()
    {
        var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.CachedSeriesAggregates.AddRange(
                SeriesWithStills(1, aired: 177, withStill: 2),
                SeriesWithStills(2, aired: 30, withStill: 6));      // 20%
            seed.CachedEpisodesExtended.AddRange(DueForAStill(101, 1, now), DueForAStill(102, 2, now));
            await seed.SaveChangesAsync();
        }

        Task<IReadOnlyList<int>> DueWith(int percent, int minAired) => NewStore().GetEpisodesNeedingRefreshAsync(
            now.AddDays(-30), now.AddHours(-12),
            new StillRecheck(now) { MinStillPercent = percent, MinAiredEpisodes = minAired }, limit: 50);

        (await DueWith(10, 10)).Should().Equal(102);
        (await DueWith(25, 10)).Should().BeEmpty("at 25% the series with 20% is skipped too");
        (await DueWith(25, 50)).Should().Equal([102], "with 50 aired episodes needed, a series of 30 is not judged yet");
        (await DueWith(0, 10)).Should().BeEquivalentTo([101, 102], "0 percent turns the rule off");
    }

    [Test]
    public async Task ASeriesThatRarelyHasStills_Should_StillGetItsStaleAndTbaRefreshes()
    {
        // Only the recheck for a still is skipped; the row is refreshed like any other.
        var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        var today = DateOnly.FromDateTime(now);

        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.CachedSeriesAggregates.Add(SeriesWithStills(1, aired: 177, withStill: 2));
            seed.CachedEpisodesExtended.AddRange(
                new CachedEpisodeExtendedEntity { EpisodeTvdbId = 101, SeriesTvdbId = 1, Name = "Old", Payload = "{}", Aired = today.AddDays(-200), HasImage = false, RetrievedUtc = now.AddDays(-31) },
                new CachedEpisodeExtendedEntity { EpisodeTvdbId = 102, SeriesTvdbId = 1, Name = "TBA", Payload = "{}", Aired = today.AddDays(3), HasImage = false, RetrievedUtc = now.AddHours(-13) });
            await seed.SaveChangesAsync();
        }

        (await NewStore().GetEpisodesNeedingRefreshAsync(now.AddDays(-30), now.AddHours(-12), new StillRecheck(now), limit: 50))
            .Should().BeEquivalentTo([101, 102]);
    }

    [Test]
    public async Task SavingAndRefreshingASeries_Should_CountItsAiredRegularEpisodes_AndThoseWithAStill()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        EpisodeSummary Ep(int id, int? season, DateOnly? aired, string? image, bool isMovie = false) =>
            new() { Id = id, SeasonNumber = season, EpisodeNumber = id, Name = $"E{id}", Aired = aired, Image = image, IsMovie = isMovie };

        var aggregate = new SeriesAggregate
        {
            TvdbId = 900, Name = "Show",
            Episodes =
            [
                Ep(1, 1, today.AddDays(-30), "https://example.test/1.jpg"),
                Ep(2, 1, today.AddDays(-20), null),
                Ep(3, 1, today, " "),                                            // aired today; a blank path is no still
                Ep(4, 1, today.AddDays(5), "https://example.test/4.jpg"),        // not aired yet
                Ep(5, 1, null, "https://example.test/5.jpg"),                    // no air date
                Ep(6, 0, today.AddDays(-10), "https://example.test/6.jpg"),      // a special
                Ep(7, 1, today.AddDays(-10), "https://example.test/7.jpg", isMovie: true),
                Ep(8, null, today.AddDays(-10), "https://example.test/8.jpg")    // no season number: not a special
            ]
        };

        var store = NewStore();
        await store.SaveSeriesAggregateAsync(aggregate, "eng");

        await using (var db = new AppDbContext(_dbOptions))
        {
            var row = await db.CachedSeriesAggregates.SingleAsync(x => x.TvdbId == 900);
            (row.AiredEpisodeCount, row.AiredStillCount).Should().Be((4, 2), "episodes 1, 2, 3 and 8 have aired; 1 and 8 have a still");
        }

        // A refresh recounts: the still for episode 2 has arrived.
        await store.UpsertSeriesAggregateAsync(
            aggregate with { Episodes = aggregate.Episodes.Select(e => e.Id == 2 ? Ep(2, 1, today.AddDays(-20), "https://example.test/2.jpg") : e).ToArray() },
            "eng");

        await using (var db = new AppDbContext(_dbOptions))
        {
            var row = await db.CachedSeriesAggregates.SingleAsync(x => x.TvdbId == 900);
            (row.AiredEpisodeCount, row.AiredStillCount).Should().Be((4, 3));
        }
    }

    [Test]
    public async Task UpsertEpisodeExtended_TracksWhetherTheEpisodeHasAStill()
    {
        var store = NewStore();

        await store.SaveEpisodeExtendedAsync(new Episode { Id = 6001, Name = "Ep", Aired = "2026-01-01", Image = null });
        await store.UpsertEpisodeExtendedAsync(new Episode { Id = 6001, Name = "Ep", Aired = "2026-01-01", Image = null });

        await using (var db = new AppDbContext(_dbOptions))
        {
            var row = await db.CachedEpisodesExtended.SingleAsync(x => x.EpisodeTvdbId == 6001);
            row.HasImage.Should().BeFalse();
            row.Aired.Should().Be(new DateOnly(2026, 1, 1));
        }

        await store.UpsertEpisodeExtendedAsync(new Episode { Id = 6001, Name = "Ep", Aired = "2026-01-01", Image = "https://example.test/still.jpg" });

        await using (var db = new AppDbContext(_dbOptions))
        {
            var row = await db.CachedEpisodesExtended.SingleAsync(x => x.EpisodeTvdbId == 6001);
            row.HasImage.Should().BeTrue("a row with a still is not rechecked for one");
        }
    }

    [Test]
    public async Task BackfillEpisodeImagesFromAggregateAsync_PatchesMissingImages()
    {
        var store = NewStore();
        await store.SaveEpisodeExtendedAsync(new Episode { Id = 7001, SeriesId = 700, Name = "No Still", Image = null });

        var aggregate = new SeriesAggregate
        {
            TvdbId = 700,
            Name = "Backfill Show",
            Episodes = [new EpisodeSummary { Id = 7001, Name = "No Still", Image = "https://example.test/screencap.jpg" }]
        };

        var patched = await store.BackfillEpisodeImagesFromAggregateAsync(aggregate);

        patched.Should().ContainSingle();
        patched[0].Image.Should().Be("https://example.test/screencap.jpg");

        var loaded = await store.GetEpisodeExtendedAsync(7001);
        loaded!.Image.Should().Be("https://example.test/screencap.jpg");

        await using var db = new AppDbContext(_dbOptions);
        var row = await db.CachedEpisodesExtended.SingleAsync(x => x.EpisodeTvdbId == 7001);
        row.HasImage.Should().BeTrue();
    }

    [Test]
    public async Task BackfillEpisodeImagesFromAggregateAsync_SkipsRows_ThatAlreadyHaveAnImage()
    {
        var store = NewStore();
        await store.SaveEpisodeExtendedAsync(new Episode { Id = 8001, SeriesId = 800, Name = "Has Still", Image = "https://example.test/original.jpg" });

        var aggregate = new SeriesAggregate
        {
            TvdbId = 800,
            Name = "No Backfill Needed",
            Episodes = [new EpisodeSummary { Id = 8001, Name = "Has Still", Image = "https://example.test/different.jpg" }]
        };

        var patched = await store.BackfillEpisodeImagesFromAggregateAsync(aggregate);

        patched.Should().BeEmpty();

        var loaded = await store.GetEpisodeExtendedAsync(8001);
        loaded!.Image.Should().Be("https://example.test/original.jpg");
    }
}
