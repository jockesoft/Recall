using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Tests.Infrastructure.Persistence.Repositories;

[TestFixture]
public sealed class AppUserRepositoryTests
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
    public async Task GetIdsWithWatches_Should_BeEveryUserWhoWatchedAnEpisodeOrAMovie_Once()
    {
        var (episodes, movies, both, nothing) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using (var seed = new AppDbContext(_dbOptions))
        {
            foreach (var (id, name) in new[] { (episodes, "episodes"), (movies, "movies"), (both, "both"), (nothing, "nothing") })
                seed.AppUsers.Add(new AppUserEntity { Id = id, Username = name, Email = $"{name}@test.local" });

            seed.EpisodeWatches.Add(new EpisodeWatchEntity { Id = Guid.NewGuid(), UserId = episodes, SeriesTvdbId = 1, EpisodeTvdbId = 11, WatchedUtc = DateTime.UtcNow });
            seed.EpisodeWatches.Add(new EpisodeWatchEntity { Id = Guid.NewGuid(), UserId = episodes, SeriesTvdbId = 1, EpisodeTvdbId = 12, WatchedUtc = DateTime.UtcNow });
            seed.EpisodeWatches.Add(new EpisodeWatchEntity { Id = Guid.NewGuid(), UserId = both, SeriesTvdbId = 1, EpisodeTvdbId = 11, WatchedUtc = DateTime.UtcNow });
            seed.UserMovieWatches.Add(new UserMovieWatchEntity { Id = Guid.NewGuid(), UserId = movies, MovieTvdbId = 5, WatchedUtc = DateTime.UtcNow });
            seed.UserMovieWatches.Add(new UserMovieWatchEntity { Id = Guid.NewGuid(), UserId = both, MovieTvdbId = 5, WatchedUtc = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        await using var db = new AppDbContext(_dbOptions);
        var ids = await new AppUserRepository(db).GetIdsWithWatchesAsync();

        ids.Should().BeEquivalentTo([episodes, movies, both]);
    }

    [Test]
    public async Task GetCountsAsync_Should_CountEveryAccount_AndTheAdminsAmongThem()
    {
        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.AppUsers.AddRange(
                new AppUserEntity { Id = Guid.NewGuid(), Username = "alice", Email = "alice@test.local", Role = UserRole.Admin },
                new AppUserEntity { Id = Guid.NewGuid(), Username = "bob", Email = "bob@test.local", Role = UserRole.User },
                new AppUserEntity { Id = Guid.NewGuid(), Username = "carol", Email = "carol@test.local" });
            await seed.SaveChangesAsync();
        }

        await using var dbContext = new AppDbContext(_dbOptions);

        (await new AppUserRepository(dbContext).GetCountsAsync()).Should().Be(new UserCounts(Total: 3, Admins: 1));
    }

    [Test]
    public async Task GetCountsAsync_Should_ReturnZeroes_WhenThereAreNoUsers()
    {
        await using var dbContext = new AppDbContext(_dbOptions);

        (await new AppUserRepository(dbContext).GetCountsAsync()).Should().Be(new UserCounts(0, 0));
    }
}
