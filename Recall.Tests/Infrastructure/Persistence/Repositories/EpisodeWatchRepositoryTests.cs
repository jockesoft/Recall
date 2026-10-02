using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using AwesomeAssertions;

namespace Recall.Tests.Infrastructure.Persistence.Repositories;

[TestFixture]
public sealed class EpisodeWatchRepositoryTests
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

        await using var dbContext = new AppDbContext(_dbOptions);
        await dbContext.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        await _connection.DisposeAsync();
    }

    [Test]
    public async Task MarkWatchedAsync_Should_PersistAndReturnWatchedEpisodes_ForSeries()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new EpisodeWatchRepository(dbContext, NullLogger<EpisodeWatchRepository>.Instance);

        await sut.MarkWatchedAsync(userId, seriesTvdbId: 100, episodeTvdbId: 1001);
        await sut.MarkWatchedAsync(userId, seriesTvdbId: 200, episodeTvdbId: 2001);

        var watchedInSeries100 = await sut.GetWatchedEpisodeIdsAsync(userId, [100]);

        watchedInSeries100.Should().BeEquivalentTo([1001]);
        (await sut.IsWatchedAsync(userId, 1001)).Should().BeTrue();
        (await sut.IsWatchedAsync(userId, 2001)).Should().BeTrue();
    }

    [Test]
    public async Task MarkUnwatchedAsync_Should_RemoveEpisodeWatch()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new EpisodeWatchRepository(dbContext, NullLogger<EpisodeWatchRepository>.Instance);

        await sut.MarkWatchedAsync(userId, seriesTvdbId: 300, episodeTvdbId: 3001);
        (await sut.IsWatchedAsync(userId, 3001)).Should().BeTrue();

        await sut.MarkUnwatchedAsync(userId, 3001);

        (await sut.IsWatchedAsync(userId, 3001)).Should().BeFalse();
    }

    [Test]
    public async Task MarkWatchedAsync_Should_RecordASingleWatch()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new EpisodeWatchRepository(dbContext, NullLogger<EpisodeWatchRepository>.Instance);

        await sut.MarkWatchedAsync(userId, seriesTvdbId: 100, episodeTvdbId: 1001);

        (await sut.GetWatchesAsync(userId)).Should().ContainSingle()
            .Which.Source.Should().Be(WatchSource.Single);
    }

    [Test]
    public async Task MarkWatchedRangeAsync_Should_RecordTheClickedEpisodeAsSingle_AndTheRestAsBulk()
    {
        // "Mark this and earlier" on 1003: it was watched now, 1001 and 1002
        // are being caught up.
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new EpisodeWatchRepository(dbContext, NullLogger<EpisodeWatchRepository>.Instance);

        var batch = await sut.MarkWatchedRangeAsync(
            userId, 100, [1001, 1002, 1003], WatchSource.Bulk, clickedEpisodeTvdbId: 1003);

        var sources = (await sut.GetWatchesAsync(userId)).ToDictionary(w => w.EpisodeTvdbId, w => w.Source);
        sources.Should().BeEquivalentTo(new Dictionary<int, WatchSource>
        {
            [1001] = WatchSource.Bulk,
            [1002] = WatchSource.Bulk,
            [1003] = WatchSource.Single
        });

        // Still one batch: the undo takes all three back.
        (await sut.UndoWatchedBatchAsync(userId, 100, batch.WatchedUtc)).Should().Be(3);
    }

    [Test]
    public async Task MarkWatchedRangeAsync_Should_RecordEveryRowWithTheGivenSource_WhenNoEpisodeWasClicked()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new EpisodeWatchRepository(dbContext, NullLogger<EpisodeWatchRepository>.Instance);

        await sut.MarkWatchedRangeAsync(userId, 100, [1001, 1002], WatchSource.Bulk);
        await sut.MarkWatchedRangeAsync(userId, 100, [1003], WatchSource.Single);

        var sources = (await sut.GetWatchesAsync(userId)).ToDictionary(w => w.EpisodeTvdbId, w => w.Source);
        sources[1001].Should().Be(WatchSource.Bulk);
        sources[1002].Should().Be(WatchSource.Bulk);
        sources[1003].Should().Be(WatchSource.Single);
    }

    [Test]
    public async Task GetWatchesAsync_Should_ReturnOnlyTheUsersRows_AcrossSeries()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        await SeedUserAsync(userId);
        await SeedUserAsync(otherUserId);

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new EpisodeWatchRepository(dbContext, NullLogger<EpisodeWatchRepository>.Instance);
        await sut.MarkWatchedAsync(userId, 100, 1001);
        await sut.MarkWatchedAsync(userId, 200, 2001);
        await sut.MarkWatchedAsync(otherUserId, 100, 1002);

        var watches = await sut.GetWatchesAsync(userId);

        watches.Select(w => (w.SeriesTvdbId, w.EpisodeTvdbId)).Should().BeEquivalentTo([(100, 1001), (200, 2001)]);
        watches.Should().OnlyContain(w => w.WatchedUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Test]
    public async Task MarkWatchedRangeAsync_Should_InsertOnlyNewEpisodes_AllWithTheBatchTimestamp()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new EpisodeWatchRepository(dbContext, NullLogger<EpisodeWatchRepository>.Instance);
        await sut.MarkWatchedAsync(userId, seriesTvdbId: 100, episodeTvdbId: 1001);

        var batch = await sut.MarkWatchedRangeAsync(userId, 100, [1001, 1002, 1003, 1003], WatchSource.Bulk);

        batch.InsertedCount.Should().Be(2, "1001 was already watched and 1003 was listed twice");
        (batch.WatchedUtc.Ticks % TimeSpan.TicksPerMillisecond).Should().Be(0,
            "the undo matches on this value, so it must survive Postgres' microsecond precision");

        var stamps = await sut.GetWatchedUtcByEpisodeAsync(userId, 100);
        stamps[1002].Should().Be(batch.WatchedUtc);
        stamps[1003].Should().Be(batch.WatchedUtc);
        stamps[1001].Should().NotBe(batch.WatchedUtc);
    }

    [Test]
    public async Task MarkWatchedRangeAsync_Should_ReturnAnEmptyBatch_WhenEverythingIsAlreadyWatched()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new EpisodeWatchRepository(dbContext, NullLogger<EpisodeWatchRepository>.Instance);
        await sut.MarkWatchedRangeAsync(userId, 100, [1001, 1002], WatchSource.Bulk);

        var second = await sut.MarkWatchedRangeAsync(userId, 100, [1001, 1002], WatchSource.Bulk);

        second.InsertedCount.Should().Be(0);
    }

    [Test]
    public async Task UndoWatchedBatchAsync_Should_RemoveOnlyTheRowsThatBatchInserted()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        await SeedUserAsync(userId);
        await SeedUserAsync(otherUserId);

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new EpisodeWatchRepository(dbContext, NullLogger<EpisodeWatchRepository>.Instance);
        await sut.MarkWatchedAsync(userId, 100, 1001);            // watched before the batch
        await sut.MarkWatchedAsync(otherUserId, 100, 1002);       // someone else's watch
        var batch = await sut.MarkWatchedRangeAsync(userId, 100, [1001, 1002, 1003], WatchSource.Bulk);
        await sut.MarkWatchedAsync(userId, 100, 1004);            // watched after the batch

        var removed = await sut.UndoWatchedBatchAsync(userId, 100, batch.WatchedUtc);

        removed.Should().Be(2);
        (await sut.GetWatchedEpisodeIdsAsync(userId, 100)).Should().BeEquivalentTo([1001, 1004]);
        (await sut.GetWatchedEpisodeIdsAsync(otherUserId, 100)).Should().BeEquivalentTo([1002]);
    }

    [Test]
    public async Task UndoWatchedBatchAsync_Should_RemoveNothing_ForAnotherSeriesOrAnUnknownTimestamp()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new EpisodeWatchRepository(dbContext, NullLogger<EpisodeWatchRepository>.Instance);
        var batch = await sut.MarkWatchedRangeAsync(userId, 100, [1001, 1002], WatchSource.Bulk);

        (await sut.UndoWatchedBatchAsync(userId, 200, batch.WatchedUtc)).Should().Be(0);
        (await sut.UndoWatchedBatchAsync(userId, 100, batch.WatchedUtc.AddMilliseconds(1))).Should().Be(0);
        (await sut.GetWatchedEpisodeIdsAsync(userId, 100)).Should().BeEquivalentTo([1001, 1002]);
    }

    [Test]
    public async Task MarkUnwatchedRangeAsync_Should_RemoveOnlyTheGivenEpisodes_ForThatUser()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        await SeedUserAsync(userId);
        await SeedUserAsync(otherUserId);

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new EpisodeWatchRepository(dbContext, NullLogger<EpisodeWatchRepository>.Instance);
        await sut.MarkWatchedRangeAsync(userId, 100, [1001, 1002, 1003], WatchSource.Bulk);
        await sut.MarkWatchedRangeAsync(otherUserId, 100, [1001], WatchSource.Bulk);

        var removed = await sut.MarkUnwatchedRangeAsync(userId, [1001, 1002, 9999]);

        removed.Should().Be(2);
        (await sut.GetWatchedEpisodeIdsAsync(userId, 100)).Should().BeEquivalentTo([1003]);
        (await sut.GetWatchedEpisodeIdsAsync(otherUserId, 100)).Should().BeEquivalentTo([1001]);
    }

    private async Task SeedUserAsync(Guid userId)
    {
        await using var dbContext = new AppDbContext(_dbOptions);
        dbContext.AppUsers.Add(new AppUserEntity
        {
            Id = userId,
            Username = $"user-{userId:N}",
            Email = $"{userId:N}@test.local"
        });

        await dbContext.SaveChangesAsync();
    }
}

