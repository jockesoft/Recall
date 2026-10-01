using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Tests.Infrastructure.Persistence.Repositories;

[TestFixture]
public sealed class TrackedMovieRepositoryTests
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

    private static TrackedMovieRepository NewSut(AppDbContext dbContext) =>
        new(dbContext, NullLogger<TrackedMovieRepository>.Instance);

    [Test]
    public async Task AddAsync_Should_PutTheMovieOnTheWatchlist()
    {
        var userId = await SeedUserAsync();

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = NewSut(dbContext);

        (await sut.AddAsync(userId, 287533, "  Oppenheimer  ")).Should().BeTrue();

        (await sut.ExistsAsync(userId, 287533)).Should().BeTrue();
        var list = await sut.GetByUserAsync(userId);
        list.Should().ContainSingle();
        list[0].MovieTvdbId.Should().Be(287533);
        list[0].Name.Should().Be("Oppenheimer");
        list[0].AddedUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task AddAsync_Should_ReturnFalse_AndKeepOneRow_WhenTheMovieIsAlreadyThere()
    {
        var userId = await SeedUserAsync();

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = NewSut(dbContext);
        await sut.AddAsync(userId, 100, "First");

        (await sut.AddAsync(userId, 100, "Again")).Should().BeFalse();

        (await sut.GetByUserAsync(userId)).Should().ContainSingle().Which.Name.Should().Be("First");
    }

    [Test]
    public async Task AddAsync_Should_TruncateATitleLongerThanTheColumn()
    {
        var userId = await SeedUserAsync();

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = NewSut(dbContext);

        await sut.AddAsync(userId, 100, new string('x', 600));

        (await sut.GetByUserAsync(userId)).Single().Name.Should().HaveLength(500);
    }

    [Test]
    public async Task Watchlists_Should_BeSeparatePerUser()
    {
        var alice = await SeedUserAsync();
        var bob = await SeedUserAsync();

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = NewSut(dbContext);
        await sut.AddAsync(alice, 100, "Shared title");

        (await sut.AddAsync(bob, 100, "Shared title")).Should().BeTrue("the same movie can be on two users' watchlists");
        (await sut.RemoveAsync(bob, 100)).Should().BeTrue();

        (await sut.ExistsAsync(alice, 100)).Should().BeTrue();
        (await sut.ExistsAsync(bob, 100)).Should().BeFalse();
    }

    [Test]
    public async Task RemoveAsync_Should_ReturnFalse_WhenTheMovieWasNotOnTheWatchlist()
    {
        var userId = await SeedUserAsync();

        await using var dbContext = new AppDbContext(_dbOptions);

        (await NewSut(dbContext).RemoveAsync(userId, 999)).Should().BeFalse();
    }

    [Test]
    public async Task GetByUserAsync_Should_ReturnMostRecentlyAddedFirst()
    {
        var userId = await SeedUserAsync();
        var now = DateTime.UtcNow;

        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.TrackedMovies.AddRange(
                new TrackedMovieEntity { Id = Guid.NewGuid(), UserId = userId, TvdbId = 1, Name = "Oldest" },
                new TrackedMovieEntity { Id = Guid.NewGuid(), UserId = userId, TvdbId = 2, Name = "Newest" },
                new TrackedMovieEntity { Id = Guid.NewGuid(), UserId = userId, TvdbId = 3, Name = "Middle" });
            await seed.SaveChangesAsync();

            // The audit hook stamps "now" on insert; spread them out afterwards.
            await seed.TrackedMovies.Where(x => x.TvdbId == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedUtc, now.AddDays(-3)));
            await seed.TrackedMovies.Where(x => x.TvdbId == 3).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedUtc, now.AddDays(-2)));
            await seed.TrackedMovies.Where(x => x.TvdbId == 2).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedUtc, now.AddDays(-1)));
        }

        await using var dbContext = new AppDbContext(_dbOptions);

        (await NewSut(dbContext).GetByUserAsync(userId)).Select(m => m.Name).Should().Equal("Newest", "Middle", "Oldest");
    }

    private async Task<Guid> SeedUserAsync()
    {
        var userId = Guid.NewGuid();

        await using var dbContext = new AppDbContext(_dbOptions);
        dbContext.AppUsers.Add(new AppUserEntity
        {
            Id = userId,
            Username = $"user-{userId:N}",
            Email = $"{userId:N}@test.local"
        });
        await dbContext.SaveChangesAsync();

        return userId;
    }
}
