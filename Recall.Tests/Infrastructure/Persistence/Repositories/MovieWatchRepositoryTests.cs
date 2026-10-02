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

        var result = await sut.ToggleAsync(userId, movieTvdbId: 287533, WatchSource.Single);

        result.Should().BeTrue();
        (await sut.GetWatchedUtcAsync(userId, 287533)).Should().NotBeNull();
    }

    [TestCase(WatchSource.Single)]
    [TestCase(WatchSource.Import)]
    public async Task ToggleAsync_Should_RecordTheSourceItWasGiven(WatchSource source)
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new MovieWatchRepository(dbContext, NullLogger<MovieWatchRepository>.Instance);

        await sut.ToggleAsync(userId, movieTvdbId: 287533, source);

        (await sut.GetWatchedMoviesAsync(userId)).Should().ContainSingle()
            .Which.Source.Should().Be(source);
    }

    [Test]
    public async Task ToggleAsync_Should_MarkUnwatched_WhenAlreadyWatched()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new MovieWatchRepository(dbContext, NullLogger<MovieWatchRepository>.Instance);

        (await sut.ToggleAsync(userId, movieTvdbId: 287533, WatchSource.Single)).Should().BeTrue();

        var result = await sut.ToggleAsync(userId, movieTvdbId: 287533, WatchSource.Single);

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

        await sut.ToggleAsync(userId, movieTvdbId: 1, WatchSource.Single);
        await sut.ToggleAsync(userId, movieTvdbId: 2, WatchSource.Single);

        (await sut.GetWatchedUtcAsync(userId, 1)).Should().NotBeNull();
        (await sut.GetWatchedUtcAsync(userId, 2)).Should().NotBeNull();

        await sut.ToggleAsync(userId, movieTvdbId: 1, WatchSource.Single);

        (await sut.GetWatchedUtcAsync(userId, 1)).Should().BeNull();
        (await sut.GetWatchedUtcAsync(userId, 2)).Should().NotBeNull();
    }

    [Test]
    public async Task GetWatchedMoviesAsync_Should_ReturnOnlyThisUsersWatches_NewestFirst()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        await SeedUserAsync(userId);
        await SeedUserAsync(otherUserId);

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new MovieWatchRepository(dbContext, NullLogger<MovieWatchRepository>.Instance);

        await sut.ToggleAsync(userId, movieTvdbId: 1, WatchSource.Single);
        await sut.ToggleAsync(userId, movieTvdbId: 2, WatchSource.Single);
        await sut.ToggleAsync(otherUserId, movieTvdbId: 3, WatchSource.Single);

        var watched = await sut.GetWatchedMoviesAsync(userId);

        watched.Select(w => w.MovieTvdbId).Should().BeEquivalentTo([1, 2]);
    }

    [Test]
    public async Task GetWatchedMoviesAsync_Should_ReturnEmpty_WhenNothingWatched()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new MovieWatchRepository(dbContext, NullLogger<MovieWatchRepository>.Instance);

        (await sut.GetWatchedMoviesAsync(userId)).Should().BeEmpty();
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
