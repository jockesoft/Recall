using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using AwesomeAssertions;
using Recall.Web.Domain.Omdb;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.OmdbCache;

namespace Recall.Tests.Infrastructure.Persistence;

[TestFixture]
public sealed class OmdbSnapshotStoreTests
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

    private OmdbSnapshotStore NewStore() =>
        new(new Factory(_dbOptions), NullLogger<OmdbSnapshotStore>.Instance);

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private async Task SeedCachedSeriesAsync(params int[] tvdbIds)
    {
        await using var db = new AppDbContext(_dbOptions);
        foreach (var id in tvdbIds)
        {
            db.CachedSeriesAggregates.Add(new CachedSeriesAggregateEntity
            {
                TvdbId = id,
                Language = "eng",
                Name = $"Series {id}",
                Payload = "{}",
                RetrievedUtc = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task Upsert_ThenGet_RoundTripsTheRecord()
    {
        var data = new OmdbSeries
        {
            Title = "Lioness",
            Year = "2023–",
            ImdbRating = "7.8",
            ImdbId = "tt13111078",
            Type = "series",
            TotalSeasons = "3",
            Response = "True",
            Ratings = [new OmdbRating { Source = "Internet Movie Database", Value = "7.8/10" }]
        };

        await NewStore().UpsertAsync(500, "tt13111078", data, CancellationToken.None);

        var loaded = await NewStore().GetAsync(500);

        loaded.Should().NotBeNull();
        loaded!.Title.Should().Be("Lioness");
        loaded.ImdbRating.Should().Be("7.8");
        loaded.Ratings.Should().ContainSingle();
        loaded.Ratings[0].Value.Should().Be("7.8/10");
    }

    [Test]
    public async Task Upsert_Overwrites_ExistingRow()
    {
        await NewStore().UpsertAsync(600, "tt1", new OmdbSeries { Title = "First", Response = "True" }, CancellationToken.None);
        await NewStore().UpsertAsync(600, "tt1", new OmdbSeries { Title = "Second", Response = "True" }, CancellationToken.None);

        (await NewStore().GetAsync(600))!.Title.Should().Be("Second");
    }

    [Test]
    public async Task Upsert_WithNullData_StoresMarkerRow_AndGetReturnsNull()
    {
        await NewStore().UpsertAsync(700, imdbId: null, data: null, CancellationToken.None);

        (await NewStore().GetAsync(700)).Should().BeNull();

        await using var read = new AppDbContext(_dbOptions);
        var row = await read.CachedSeriesOmdb.SingleAsync(x => x.TvdbId == 700);
        row.Payload.Should().BeNull();
        row.RetrievedUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Test]
    public async Task GetSeriesNeedingOmdb_ReturnsMissingAndStale_ExcludesFresh()
    {
        await SeedCachedSeriesAsync(1, 2, 3);

        var store = NewStore();
        // series 2: fresh (should be excluded)
        await store.UpsertAsync(2, "tt2", new OmdbSeries { Title = "Fresh", Response = "True" }, CancellationToken.None);
        // series 3: stale (should be included)
        await store.UpsertAsync(3, "tt3", new OmdbSeries { Title = "Stale", Response = "True" }, CancellationToken.None);
        await using (var db = new AppDbContext(_dbOptions))
        {
            var stale = await db.CachedSeriesOmdb.SingleAsync(x => x.TvdbId == 3);
            stale.RetrievedUtc = DateTime.UtcNow.AddDays(-40);
            await db.SaveChangesAsync();
        }

        var due = await store.GetSeriesNeedingOmdbAsync(DateTime.UtcNow.AddDays(-30), limit: 10, CancellationToken.None);

        due.Should().Equal(1, 3); // 1 (never fetched) and 3 (stale); 2 excluded as fresh
    }

    [Test]
    public async Task GetSeriesNeedingOmdb_RespectsLimit()
    {
        await SeedCachedSeriesAsync(10, 11, 12, 13);

        var due = await NewStore().GetSeriesNeedingOmdbAsync(DateTime.UtcNow.AddDays(-30), limit: 2, CancellationToken.None);

        due.Should().HaveCount(2);
    }
}
