using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using AwesomeAssertions;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Services.Sitemap;

namespace Recall.Tests.Services.Sitemap;

[TestFixture]
public sealed class SitemapServiceTests
{
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

    private SitemapService NewService() => new(new Factory(_dbOptions));

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    [Test]
    public async Task GetCachedSeriesAsync_Should_ReturnEmpty_WhenNothingCached()
    {
        (await NewService().GetCachedSeriesAsync(limit: 100)).Should().BeEmpty();
    }

    [Test]
    public async Task GetCachedSeriesAsync_Should_DedupeByTvdbId_KeepingLatestRetrievedUtc()
    {
        var now = DateTime.UtcNow;

        await using (var db = new AppDbContext(_dbOptions))
        {
            db.CachedSeriesAggregates.AddRange(
                new CachedSeriesAggregateEntity { TvdbId = 1, Language = "eng", Name = "A", Payload = "{}", RetrievedUtc = now.AddDays(-5) },
                new CachedSeriesAggregateEntity { TvdbId = 1, Language = "spa", Name = "A", Payload = "{}", RetrievedUtc = now },
                new CachedSeriesAggregateEntity { TvdbId = 2, Language = "eng", Name = "B", Payload = "{}", RetrievedUtc = now.AddDays(-1) });
            await db.SaveChangesAsync();
        }

        var result = await NewService().GetCachedSeriesAsync(limit: 100);

        result.Should().HaveCount(2);
        result.Single(e => e.TvdbId == 1).LastModifiedUtc.Should().BeCloseTo(now, TimeSpan.FromSeconds(1));
        result.Single(e => e.TvdbId == 2).LastModifiedUtc.Should().BeCloseTo(now.AddDays(-1), TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task GetCachedMoviesAsync_Should_ReturnOneEntryPerMovie()
    {
        var now = DateTime.UtcNow;

        await using (var db = new AppDbContext(_dbOptions))
        {
            db.CachedMovieAggregates.Add(
                new CachedMovieAggregateEntity { TvdbId = 287533, Language = "eng", Name = "Oppenheimer", Payload = "{}", RetrievedUtc = now });
            await db.SaveChangesAsync();
        }

        var result = await NewService().GetCachedMoviesAsync(limit: 100);

        result.Should().ContainSingle().Which.TvdbId.Should().Be(287533);
    }

    [Test]
    public async Task GetCachedEpisodesAsync_Should_ReturnOneEntryPerEpisode()
    {
        var now = DateTime.UtcNow;

        await using (var db = new AppDbContext(_dbOptions))
        {
            db.CachedEpisodesExtended.AddRange(
                new CachedEpisodeExtendedEntity { EpisodeTvdbId = 100, Name = "Ep1", Payload = "{}", RetrievedUtc = now },
                new CachedEpisodeExtendedEntity { EpisodeTvdbId = 200, Name = "Ep2", Payload = "{}", RetrievedUtc = now.AddHours(-2) });
            await db.SaveChangesAsync();
        }

        var result = await NewService().GetCachedEpisodesAsync(limit: 100);

        result.Should().HaveCount(2);
        result.Select(e => e.TvdbId).Should().BeEquivalentTo([100, 200]);
    }

    private async Task SeedAsync(int series, int movies, int episodes)
    {
        // Lower ids are the most recently refreshed, in every table.
        var now = DateTime.UtcNow;

        await using var db = new AppDbContext(_dbOptions);
        db.CachedSeriesAggregates.AddRange(Enumerable.Range(1, series).Select(i =>
            new CachedSeriesAggregateEntity { TvdbId = i, Language = "eng", Name = $"S{i}", Payload = "{}", RetrievedUtc = now.AddHours(-i) }));
        db.CachedMovieAggregates.AddRange(Enumerable.Range(1, movies).Select(i =>
            new CachedMovieAggregateEntity { TvdbId = i, Language = "eng", Name = $"M{i}", Payload = "{}", RetrievedUtc = now.AddHours(-i) }));
        db.CachedEpisodesExtended.AddRange(Enumerable.Range(1, episodes).Select(i =>
            new CachedEpisodeExtendedEntity { EpisodeTvdbId = i, Name = $"E{i}", Payload = "{}", RetrievedUtc = now.AddHours(-i) }));
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task GetCachedContentAsync_Should_ReturnEverything_WhenItFits()
    {
        await SeedAsync(series: 3, movies: 2, episodes: 4);

        var content = await NewService().GetCachedContentAsync(maxEntries: 100);

        content.Series.Should().HaveCount(3);
        content.Movies.Should().HaveCount(2);
        content.Episodes.Should().HaveCount(4);
        content.Count.Should().Be(9);
    }

    [Test]
    public async Task GetCachedContentAsync_Should_FillSeriesAndMoviesFirst_ThenEpisodesWithWhatIsLeft()
    {
        await SeedAsync(series: 3, movies: 2, episodes: 10);

        var content = await NewService().GetCachedContentAsync(maxEntries: 8);

        content.Series.Should().HaveCount(3);
        content.Movies.Should().HaveCount(2);
        content.Episodes.Select(e => e.TvdbId).Should().Equal([1, 2, 3], "only three slots remain, and they go to the most recently refreshed episodes");
        content.Count.Should().Be(8);
    }

    [Test]
    public async Task GetCachedContentAsync_Should_DropEpisodesEntirely_WhenSeriesAndMoviesUseTheWholeAllowance()
    {
        await SeedAsync(series: 3, movies: 2, episodes: 10);

        var content = await NewService().GetCachedContentAsync(maxEntries: 5);

        content.Series.Should().HaveCount(3);
        content.Movies.Should().HaveCount(2);
        content.Episodes.Should().BeEmpty();
    }

    [Test]
    public async Task GetCachedContentAsync_Should_PutSeriesBeforeMovies_WhenEvenThoseDoNotAllFit()
    {
        await SeedAsync(series: 3, movies: 4, episodes: 10);

        var content = await NewService().GetCachedContentAsync(maxEntries: 5);

        content.Series.Should().HaveCount(3);
        content.Movies.Select(e => e.TvdbId).Should().Equal(1, 2);
        content.Episodes.Should().BeEmpty();

        var tighter = await NewService().GetCachedContentAsync(maxEntries: 2);

        tighter.Series.Select(e => e.TvdbId).Should().Equal(1, 2);
        tighter.Movies.Should().BeEmpty();
        tighter.Count.Should().Be(2);
    }

    [Test]
    public async Task GetCachedContentAsync_Should_NeverExceedTheLimit_AndCopeWithZeroOrLess()
    {
        await SeedAsync(series: 3, movies: 2, episodes: 10);

        (await NewService().GetCachedContentAsync(maxEntries: 0)).Count.Should().Be(0);
        (await NewService().GetCachedContentAsync(maxEntries: -5)).Count.Should().Be(0);

        for (var max = 1; max <= 16; max++)
            (await NewService().GetCachedContentAsync(max)).Count.Should().Be(Math.Min(max, 15));
    }

    [Test]
    public async Task EachKind_Should_BeOrderedMostRecentlyRefreshedFirst_AndHonorItsLimit()
    {
        await SeedAsync(series: 5, movies: 5, episodes: 5);
        var service = NewService();

        (await service.GetCachedSeriesAsync(limit: 3)).Select(e => e.TvdbId).Should().Equal(1, 2, 3);
        (await service.GetCachedMoviesAsync(limit: 3)).Select(e => e.TvdbId).Should().Equal(1, 2, 3);
        (await service.GetCachedEpisodesAsync(limit: 3)).Select(e => e.TvdbId).Should().Equal(1, 2, 3);
    }
}
