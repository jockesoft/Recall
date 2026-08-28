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

    private TvdbSnapshotStore NewStore(AppDbContext db) => new(db, NullLogger<TvdbSnapshotStore>.Instance);

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

        await using (var write = new AppDbContext(_dbOptions))
            await NewStore(write).SaveSeriesAggregateAsync(aggregate, "eng");

        await using var read = new AppDbContext(_dbOptions);
        var loaded = await NewStore(read).GetSeriesAggregateAsync(100, "eng");

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
        await using (var first = new AppDbContext(_dbOptions))
            await NewStore(first).SaveSeriesAggregateAsync(new SeriesAggregate { TvdbId = 200, Name = "Original" }, "eng");

        await using (var second = new AppDbContext(_dbOptions))
            await NewStore(second).SaveSeriesAggregateAsync(new SeriesAggregate { TvdbId = 200, Name = "Replacement" }, "eng");

        await using var read = new AppDbContext(_dbOptions);
        var loaded = await NewStore(read).GetSeriesAggregateAsync(200, "eng");

        loaded!.Name.Should().Be("Original");
    }

    [Test]
    public async Task GetSeriesAggregate_ReturnsNull_WhenMissing()
    {
        await using var db = new AppDbContext(_dbOptions);
        (await NewStore(db).GetSeriesAggregateAsync(999, "eng")).Should().BeNull();
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

        await using var db = new AppDbContext(_dbOptions);
        (await NewStore(db).GetSeriesAggregateAsync(300, "eng")).Should().BeNull();
    }

    [Test]
    public async Task SaveThenGet_SeriesExtended_And_EpisodeExtended_RoundTrip()
    {
        await using (var write = new AppDbContext(_dbOptions))
        {
            var store = NewStore(write);
            await store.SaveSeriesExtendedAsync(new Series { Id = 400, Name = "Extended", Slug = "ext" });
            await store.SaveEpisodeExtendedAsync(new Episode { Id = 4001, SeriesId = 400, Name = "Ep One" });
        }

        await using var read = new AppDbContext(_dbOptions);
        var readStore = NewStore(read);

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
        await using (var write = new AppDbContext(_dbOptions))
            await NewStore(write).SaveEpisodeExtendedAsync(new Episode { Id = null, Name = "No Id" });

        await using var read = new AppDbContext(_dbOptions);
        (await read.CachedEpisodesExtended.CountAsync()).Should().Be(0);
    }
}
