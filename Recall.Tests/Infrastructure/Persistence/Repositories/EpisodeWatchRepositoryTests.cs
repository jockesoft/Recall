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

    private async Task SeedUserAsync(Guid userId)
    {
        await using var dbContext = new AppDbContext(_dbOptions);
        dbContext.AppUsers.Add(new AppUserEntity
        {
            Id = userId,
            UserId = $"external-{userId}",
            Username = $"user-{userId:N}",
            Email = $"{userId:N}@test.local",
            Password = "placeholder"
        });

        await dbContext.SaveChangesAsync();
    }
}

