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
            limit: 10);

        // 1: older than 30d. 2: still "TBA" and older than 12h.
        // 3: "TBA" but only 3h old. 4: fresh and titled.
        due.Should().BeEquivalentTo(new[] { 1, 2 });
    }
}
