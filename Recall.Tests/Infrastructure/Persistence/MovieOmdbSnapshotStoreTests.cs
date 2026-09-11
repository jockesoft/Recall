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
public sealed class MovieOmdbSnapshotStoreTests
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

    private MovieOmdbSnapshotStore NewStore() =>
        new(new Factory(_dbOptions), NullLogger<MovieOmdbSnapshotStore>.Instance);

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private async Task SeedCachedMoviesAsync(params int[] tvdbIds)
    {
        await using var db = new AppDbContext(_dbOptions);
        foreach (var id in tvdbIds)
        {
            db.CachedMovieAggregates.Add(new CachedMovieAggregateEntity
            {
                TvdbId = id,
                Language = "eng",
                Name = $"Movie {id}",
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
            Title = "Oppenheimer",
            Year = "2023",
            ImdbRating = "8.4",
            ImdbId = "tt15398776",
            Type = "movie",
            Response = "True",
            Awards = "Won 7 Oscars",
            Ratings = [new OmdbRating { Source = "Internet Movie Database", Value = "8.4/10" }]
        };

        await NewStore().UpsertAsync(287533, "tt15398776", data, CancellationToken.None);

        var loaded = await NewStore().GetAsync(287533);

        loaded.Should().NotBeNull();
        loaded!.Title.Should().Be("Oppenheimer");
        loaded.ImdbRating.Should().Be("8.4");
        loaded.Awards.Should().Be("Won 7 Oscars");
        loaded.Ratings.Should().ContainSingle();
        loaded.Ratings[0].Value.Should().Be("8.4/10");
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
        var row = await read.CachedMoviesOmdb.SingleAsync(x => x.TvdbId == 700);
        row.Payload.Should().BeNull();
        row.RetrievedUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Test]
    public async Task GetMoviesNeedingOmdb_ReturnsMissingAndStale_ExcludesFresh()
    {
        await SeedCachedMoviesAsync(1, 2, 3);

        var store = NewStore();
        // movie 2: fresh (should be excluded)
        await store.UpsertAsync(2, "tt2", new OmdbSeries { Title = "Fresh", Response = "True" }, CancellationToken.None);
        // movie 3: stale (should be included)
        await store.UpsertAsync(3, "tt3", new OmdbSeries { Title = "Stale", Response = "True" }, CancellationToken.None);
        await using (var db = new AppDbContext(_dbOptions))
        {
            var stale = await db.CachedMoviesOmdb.SingleAsync(x => x.TvdbId == 3);
            stale.RetrievedUtc = DateTime.UtcNow.AddDays(-40);
            await db.SaveChangesAsync();
        }

        var due = await store.GetMoviesNeedingOmdbAsync(DateTime.UtcNow.AddDays(-30), limit: 10, CancellationToken.None);

        due.Should().Equal(1, 3); // 1 (never fetched) and 3 (stale); 2 excluded as fresh
    }

    [Test]
    public async Task GetMoviesNeedingOmdb_RespectsLimit()
    {
        await SeedCachedMoviesAsync(10, 11, 12, 13);

        var due = await NewStore().GetMoviesNeedingOmdbAsync(DateTime.UtcNow.AddDays(-30), limit: 2, CancellationToken.None);

        due.Should().HaveCount(2);
    }
}
