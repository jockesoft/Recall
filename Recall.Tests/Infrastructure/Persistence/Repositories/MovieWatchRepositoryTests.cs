using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using AwesomeAssertions;

namespace Recall.Tests.Infrastructure.Persistence.Repositories;

[TestFixture]
public sealed class MovieWatchRepositoryTests
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
    public async Task ToggleAsync_Should_MarkWatched_WhenNotAlreadyWatched()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new MovieWatchRepository(dbContext, NullLogger<MovieWatchRepository>.Instance);

        var result = await sut.ToggleAsync(userId, movieTvdbId: 287533);

        result.Should().BeTrue();
        (await sut.GetWatchedUtcAsync(userId, 287533)).Should().NotBeNull();
    }

    [Test]
    public async Task ToggleAsync_Should_MarkUnwatched_WhenAlreadyWatched()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new MovieWatchRepository(dbContext, NullLogger<MovieWatchRepository>.Instance);

        (await sut.ToggleAsync(userId, movieTvdbId: 287533)).Should().BeTrue();

        var result = await sut.ToggleAsync(userId, movieTvdbId: 287533);

        result.Should().BeFalse();
        (await sut.GetWatchedUtcAsync(userId, 287533)).Should().BeNull();
    }

    [Test]
    public async Task GetWatchedUtcAsync_Should_ReturnNull_WhenNeverWatched()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new MovieWatchRepository(dbContext, NullLogger<MovieWatchRepository>.Instance);

        (await sut.GetWatchedUtcAsync(userId, 999)).Should().BeNull();
    }

    [Test]
    public async Task ToggleAsync_Should_ScopePerMovie_NotAffectingOtherMovies()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new MovieWatchRepository(dbContext, NullLogger<MovieWatchRepository>.Instance);

        await sut.ToggleAsync(userId, movieTvdbId: 1);
        await sut.ToggleAsync(userId, movieTvdbId: 2);

        (await sut.GetWatchedUtcAsync(userId, 1)).Should().NotBeNull();
        (await sut.GetWatchedUtcAsync(userId, 2)).Should().NotBeNull();

        await sut.ToggleAsync(userId, movieTvdbId: 1);

        (await sut.GetWatchedUtcAsync(userId, 1)).Should().BeNull();
        (await sut.GetWatchedUtcAsync(userId, 2)).Should().NotBeNull();
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
