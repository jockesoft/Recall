using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Tests.Infrastructure.Persistence.Repositories;

/// <summary>
/// Discover's two repositories on SQLite: the stored lists (saved, replaced,
/// read by key, an unreadable one left out, old unused ones deleted) and what
/// one user already knows and has hidden. What only PostgreSQL can show (the
/// unique-violation branches, the jsonb column) is in <c>Recall.Tests.Postgres</c>.
/// </summary>
[TestFixture]
public sealed class DiscoverRepositoryTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 5, 0, 0, DateTimeKind.Utc);

    private SqliteConnection _connection = null!;
    private DbContextOptions<AppDbContext> _options = null!;

    [SetUp]
    public async Task SetUpAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        await using var db = new AppDbContext(_options);
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public async Task TearDownAsync() => await _connection.DisposeAsync();

    // ---- lists ----------------------------------------------------------------------

    [Test]
    public async Task AList_Should_ComeBackAsSaved_InItsOrder()
    {
        var titles = new[] { Title(1, "Severance", 2022), Title(2, "Dune: Part Two", 2024, DiscoverKind.Movie) };
        await using (var db = new AppDbContext(_options))
            await Lists(db).SaveAsync("series-popular-2026", titles, Now);

        await using var read = new AppDbContext(_options);
        var lists = await Lists(read).GetAsync(["series-popular-2026", "series-new-2026"]);

        lists.Should().ContainKey("series-popular-2026").And.HaveCount(1, "a list never fetched is not there");
        lists["series-popular-2026"].Titles.Should().Equal(titles);
        lists["series-popular-2026"].FetchedUtc.Should().Be(Now);
    }

    [Test]
    public async Task SavingAgain_Should_ReplaceTheList()
    {
        await using (var db = new AppDbContext(_options))
        {
            await Lists(db).SaveAsync("movie-year-2026", [Title(1, "Old", 2026)], Now.AddDays(-1));
            await Lists(db).SaveAsync("movie-year-2026", [Title(2, "New", 2026), Title(3, "Newer", 2026)], Now);
        }

        await using var read = new AppDbContext(_options);
        var list = (await Lists(read).GetAsync(["movie-year-2026"]))["movie-year-2026"];
        list.Titles.Select(t => t.Name).Should().Equal("New", "Newer");
        list.FetchedUtc.Should().Be(Now);
        (await read.DiscoverLists.SingleAsync()).ItemCount.Should().Be(2);
        (await Lists(read).GetFetchedUtcAsync()).Should().Equal(new Dictionary<string, DateTime> { ["movie-year-2026"] = Now });
    }

    [Test]
    public async Task AnUnreadableList_Should_BeLeftOut()
    {
        await using (var db = new AppDbContext(_options))
        {
            db.DiscoverLists.Add(new DiscoverListEntity { Key = "series-alltime", Payload = "{not json", FetchedUtc = Now });
            await db.SaveChangesAsync();
        }

        await using var read = new AppDbContext(_options);
        (await Lists(read).GetAsync(["series-alltime"])).Should().BeEmpty();
    }

    [Test]
    public async Task DeleteUnused_Should_RemoveOnlyOldListsNotInUse()
    {
        await using (var db = new AppDbContext(_options))
        {
            var lists = Lists(db);
            await lists.SaveAsync("series-popular-2025", [Title(1, "A", 2025)], Now.AddDays(-40));  // unused, old: goes
            await lists.SaveAsync("series-genre-26", [Title(2, "B", 2020)], Now.AddDays(-2));       // unused, recent: stays
            await lists.SaveAsync("series-popular-2026", [Title(3, "C", 2026)], Now.AddDays(-40));  // in use: stays
        }

        await using var read = new AppDbContext(_options);
        var deleted = await Lists(read).DeleteUnusedAsync(["series-popular-2026"], Now.AddDays(-30));

        deleted.Should().Be(1);
        (await read.DiscoverLists.Select(x => x.Key).ToListAsync()).Should().BeEquivalentTo("series-genre-26", "series-popular-2026");
    }

    // ---- what a user knows, and hides -----------------------------------------------

    [Test]
    public async Task KnownTitles_Should_CoverEveryWayAUserKnowsATitle_AndOnlyTheirs()
    {
        var user = await SeedUserAsync();
        var other = await SeedUserAsync();

        await using (var db = new AppDbContext(_options))
        {
            await TrackSeriesAsync(db, user, 100);                                   // in the library
            db.EpisodeWatches.Add(Watch(user, series: 101, episode: 10101));        // watched, removed from the library
            db.EpisodeWatches.Add(Watch(user, series: 101, episode: 10102));
            db.TrackedMovies.Add(new TrackedMovieEntity { Id = Guid.NewGuid(), UserId = user, TvdbId = 200, Name = "On the watchlist" });
            db.UserMovieWatches.Add(new UserMovieWatchEntity { Id = Guid.NewGuid(), UserId = user, MovieTvdbId = 201, WatchedUtc = Now });
            db.UserLikes.Add(Like(user, LikeTargetType.Series, 102));
            db.UserLikes.Add(Like(user, LikeTargetType.Movie, 202));
            db.UserLikes.Add(Like(user, LikeTargetType.Episode, 999));             // an episode is not a title
            db.UserRatings.Add(Rating(user, RatingTargetType.Series, 103));
            db.UserRatings.Add(Rating(user, RatingTargetType.Movie, 203));
            db.UserRatings.Add(Rating(user, RatingTargetType.Episode, 998));
            db.DiscoverDismissals.Add(Hidden(user, DiscoverKind.Series, 104));
            db.DiscoverDismissals.Add(Hidden(user, DiscoverKind.Movie, 204));

            db.UserLikes.Add(Like(other, LikeTargetType.Series, 900));             // someone else's
            db.DiscoverDismissals.Add(Hidden(other, DiscoverKind.Movie, 901));
            await db.SaveChangesAsync();
        }

        await using var read = new AppDbContext(_options);
        var known = await Users(read).GetKnownTitlesAsync(user);

        known.SeriesIds.Should().BeEquivalentTo([100, 101, 102, 103, 104]);
        known.MovieIds.Should().BeEquivalentTo([200, 201, 202, 203, 204]);
        known.Contains(DiscoverKind.Movie, 200).Should().BeTrue();
        known.Contains(DiscoverKind.Series, 200).Should().BeFalse("ids are only compared within a kind");
    }

    [Test]
    public async Task AUserWithNothing_Should_KnowNothing()
    {
        var user = await SeedUserAsync();

        await using var db = new AppDbContext(_options);
        var known = await Users(db).GetKnownTitlesAsync(user);

        known.SeriesIds.Should().BeEmpty();
        known.MovieIds.Should().BeEmpty();
    }

    [Test]
    public async Task Hide_Should_StoreOnce_AndUnhide_Should_RemoveIt()
    {
        var user = await SeedUserAsync();

        await using (var db = new AppDbContext(_options))
        {
            var users = Users(db);
            (await users.HideAsync(user, DiscoverKind.Movie, 338552)).Should().BeTrue();
            (await users.HideAsync(user, DiscoverKind.Movie, 338552)).Should().BeFalse("it was already hidden");
            (await users.HideAsync(user, DiscoverKind.Series, 338552)).Should().BeTrue("a series with the same id is another title");
        }

        await using (var db = new AppDbContext(_options))
        {
            var row = await db.DiscoverDismissals.FirstAsync(x => x.Kind == DiscoverKind.Movie);
            row.CreatedUtc.Should().Be(Now, "the date comes from the TimeProvider");
            (await db.DiscoverDismissals.CountAsync()).Should().Be(2);

            (await Users(db).UnhideAsync(user, DiscoverKind.Movie, 338552)).Should().BeTrue();
            (await Users(db).UnhideAsync(user, DiscoverKind.Movie, 338552)).Should().BeFalse("nothing left to undo");
        }

        await using var read = new AppDbContext(_options);
        (await read.DiscoverDismissals.Select(x => x.Kind).ToListAsync()).Should().Equal(DiscoverKind.Series);
    }

    // ---- helpers --------------------------------------------------------------------

    private static DiscoverListRepository Lists(AppDbContext db) => new(db, NullLogger<DiscoverListRepository>.Instance);

    private static DiscoverUserRepository Users(AppDbContext db) =>
        new(db, new FixedTimeProvider(Now), NullLogger<DiscoverUserRepository>.Instance);

    private static DiscoverTitle Title(int id, string name, int year, DiscoverKind kind = DiscoverKind.Series) => new()
    {
        Kind = kind, TvdbId = id, Name = name, Year = year, ImageUrl = $"https://artworks.thetvdb.com/{id}.jpg",
        FirstAired = kind == DiscoverKind.Series ? new DateOnly(year, 1, 15) : null, Status = "Ended", Score = 1000 + id
    };

    private async Task<Guid> SeedUserAsync()
    {
        var id = Guid.NewGuid();
        await using var db = new AppDbContext(_options);
        db.AppUsers.Add(new AppUserEntity { Id = id, Username = $"u{id:N}", Email = $"{id:N}@test.local" });
        await db.SaveChangesAsync();
        return id;
    }

    // tracked_series has an xmin column that SQLite treats as an ordinary NOT NULL one.
    private static Task TrackSeriesAsync(AppDbContext db, Guid user, int tvdbId) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO tracked_series (id, user_id, tvdb_id, name, created_utc, updated_utc, xmin)
             VALUES ({Guid.NewGuid()}, {user}, {tvdbId}, {"Tracked"}, {Now}, {Now}, 1)
             """);

    private static EpisodeWatchEntity Watch(Guid user, int series, int episode) => new()
    {
        Id = Guid.NewGuid(), UserId = user, SeriesTvdbId = series, EpisodeTvdbId = episode, WatchedUtc = Now
    };

    private static UserLikeEntity Like(Guid user, LikeTargetType type, int id) => new()
    {
        Id = Guid.NewGuid(), UserId = user, TargetType = type, TargetTvdbId = id, SeriesTvdbId = id
    };

    private static UserRatingEntity Rating(Guid user, RatingTargetType type, int id) => new()
    {
        Id = Guid.NewGuid(), UserId = user, TargetType = type, TargetTvdbId = id, SeriesTvdbId = id, Value = 8
    };

    private static DiscoverDismissalEntity Hidden(Guid user, DiscoverKind kind, int id) => new()
    {
        Id = Guid.NewGuid(), UserId = user, Kind = kind, TvdbId = id, CreatedUtc = Now
    };
}
