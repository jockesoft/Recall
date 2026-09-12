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
        (await NewService().GetCachedSeriesAsync()).Should().BeEmpty();
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

        var result = await NewService().GetCachedSeriesAsync();

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

        var result = await NewService().GetCachedMoviesAsync();

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

        var result = await NewService().GetCachedEpisodesAsync();

        result.Should().HaveCount(2);
        result.Select(e => e.TvdbId).Should().BeEquivalentTo([100, 200]);
    }
}
