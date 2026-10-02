using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Services;

/// <summary>
/// <see cref="WatchProgressService"/> over a real <see cref="EpisodeWatchRepository"/>
/// (in-memory SQLite), asserting on the rows that end up stored rather than on
/// mock calls.
/// </summary>
[TestFixture]
public sealed class WatchProgressServicePersistenceTests
{
    private const int SeriesId = 42;

    private SqliteConnection _connection = null!;
    private DbContextOptions<AppDbContext> _dbOptions = null!;
    private Mock<ITheTvDbService> _tvDbService = null!;
    private Guid _userId;

    [SetUp]
    public async Task SetUpAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using var dbContext = new AppDbContext(_dbOptions);
        await dbContext.Database.EnsureCreatedAsync();

        _userId = Guid.NewGuid();
        dbContext.AppUsers.Add(new AppUserEntity
        {
            Id = _userId,
            Username = $"user-{_userId:N}",
            Email = $"{_userId:N}@test.local"
        });
        await dbContext.SaveChangesAsync();

        _tvDbService = new Mock<ITheTvDbService>();
        _tvDbService
            .Setup(x => x.GetSeriesAggregateByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate
            {
                TvdbId = SeriesId,
                Episodes =
                [
                    new EpisodeSummary { Id = 10, SeasonNumber = 0, EpisodeNumber = 1, Aired = new DateOnly(2999, 1, 1) },
                    new EpisodeSummary { Id = 1, SeasonNumber = 1, EpisodeNumber = 1, Aired = new DateOnly(2025, 1, 1) },
                    new EpisodeSummary { Id = 2, SeasonNumber = 1, EpisodeNumber = 2, Aired = new DateOnly(2025, 1, 8) },
                    new EpisodeSummary { Id = 3, SeasonNumber = 1, EpisodeNumber = 3, Aired = new DateOnly(2025, 1, 15) }
                ]
            });
        _tvDbService
            .Setup(x => x.GetEpisodeDetailsAsync(7001, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Episode { Id = 7001, SeriesId = 700, Aired = "2025-01-01" });
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        await _connection.DisposeAsync();
    }

    [Test]
    public async Task ToggleEpisodeWatchedAsync_Should_StoreThenRemoveTheWatch()
    {
        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = CreateSut(dbContext);

        (await sut.ToggleEpisodeWatchedAsync(_userId, SeriesId, 1)).Outcome.Should().Be(EpisodeWatchOutcome.MarkedWatched);
        (await StoredWatchesAsync()).Should().BeEquivalentTo([(SeriesId, 1)]);

        (await sut.ToggleEpisodeWatchedAsync(_userId, SeriesId, 1)).Outcome.Should().Be(EpisodeWatchOutcome.MarkedUnwatched);
        (await StoredWatchesAsync()).Should().BeEmpty();
    }

    [Test]
    public async Task MarkEpisodeWatchedAsync_Should_StoreNothing_ForAnEpisodeOfAnotherSeries()
    {
        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = CreateSut(dbContext);

        var outcome = await sut.MarkEpisodeWatchedAsync(_userId, SeriesId, 7001);

        outcome.Outcome.Should().Be(EpisodeWatchOutcome.EpisodeNotInSeries);
        (await StoredWatchesAsync()).Should().BeEmpty();
    }

    [Test]
    public async Task MarkWatchedThroughAsync_Should_StoreOnlyAiredEpisodes_AndSkipOnesAlreadyWatched()
    {
        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = CreateSut(dbContext);
        await sut.MarkEpisodeWatchedAsync(_userId, SeriesId, 1);

        var result = await sut.MarkWatchedThroughAsync(_userId, SeriesId, 3);

        result.MarkedCount.Should().Be(3);
        (await StoredWatchesAsync()).Should().BeEquivalentTo([(SeriesId, 1), (SeriesId, 2), (SeriesId, 3)]);
    }

    [Test]
    public async Task UndoWatchedBatchAsync_Should_PutASeasonBackExactlyAsItWas()
    {
        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = CreateSut(dbContext);
        await sut.MarkEpisodeWatchedAsync(_userId, SeriesId, 2);

        var season = await sut.MarkSeasonWatchedAsync(_userId, SeriesId, seasonNumber: 1);
        season.Batch.InsertedCount.Should().Be(2);
        (await StoredWatchesAsync()).Should().BeEquivalentTo([(SeriesId, 1), (SeriesId, 2), (SeriesId, 3)]);

        var removed = await sut.UndoWatchedBatchAsync(_userId, SeriesId, season.Batch.WatchedUtc);

        removed.Should().Be(2);
        (await StoredWatchesAsync()).Should().BeEquivalentTo([(SeriesId, 2)], "the episode watched before the bulk mark stays watched");
    }

    [Test]
    public async Task MarkSeasonUnwatchedAsync_Should_ClearTheSeason()
    {
        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = CreateSut(dbContext);
        await sut.MarkSeasonWatchedAsync(_userId, SeriesId, seasonNumber: 1);

        var removed = await sut.MarkSeasonUnwatchedAsync(_userId, SeriesId, seasonNumber: 1);

        removed.Should().Be(3);
        (await StoredWatchesAsync()).Should().BeEmpty();
    }

    private WatchProgressService CreateSut(AppDbContext dbContext) =>
        new(
            _tvDbService.Object,
            new EpisodeWatchRepository(dbContext, NullLogger<EpisodeWatchRepository>.Instance),
            // tracked_series cannot be inserted through EF on SQLite (xmin); the library is not this fixture's subject.
            Mock.Of<ITrackedSeriesRepository>(r => r.ExistsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()) == Task.FromResult(true)),
            new RatingRepository(dbContext, NullLogger<RatingRepository>.Instance),
            TimeProvider.System,
            NullLogger<WatchProgressService>.Instance);

    private async Task<List<(int SeriesTvdbId, int EpisodeTvdbId)>> StoredWatchesAsync()
    {
        await using var dbContext = new AppDbContext(_dbOptions);
        var rows = await dbContext.EpisodeWatches
            .AsNoTracking()
            .Where(x => x.UserId == _userId)
            .Select(x => new { x.SeriesTvdbId, x.EpisodeTvdbId })
            .ToListAsync();

        return rows.Select(r => (r.SeriesTvdbId, r.EpisodeTvdbId)).ToList();
    }
}
