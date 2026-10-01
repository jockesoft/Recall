using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Tests.Infrastructure.Persistence.Repositories;

[TestFixture]
public sealed class RatingRepositoryTests
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

    private static RatingRepository NewSut(AppDbContext dbContext) =>
        new(dbContext, NullLogger<RatingRepository>.Instance);

    [Test]
    public async Task GetSummaryAsync_Should_ReturnEmpty_WhenNobodyHasRatedTheTarget()
    {
        await using var dbContext = new AppDbContext(_dbOptions);

        var summary = await NewSut(dbContext).GetSummaryAsync(RatingTargetType.Series, 42);

        summary.Should().Be(RatingSummary.Empty);
        summary.Average.Should().BeNull();
        summary.Count.Should().Be(0);
    }

    [Test]
    public async Task GetSummaryAsync_Should_ReturnTheCountAndAverage_OfEveryUsersRating()
    {
        var alice = await SeedUserAsync();
        var bob = await SeedUserAsync();
        var carol = await SeedUserAsync();

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = NewSut(dbContext);
        await sut.RateAsync(alice, RatingTargetType.Series, 42, 42, 10);
        await sut.RateAsync(bob, RatingTargetType.Series, 42, 42, 7);
        await sut.RateAsync(carol, RatingTargetType.Series, 42, 42, 8);

        var summary = await sut.GetSummaryAsync(RatingTargetType.Series, 42);

        summary.Count.Should().Be(3);
        summary.Average.Should().BeApproximately(25 / 3.0, 0.0001, "the average must not be truncated to a whole number");
    }

    [Test]
    public async Task GetSummaryAsync_Should_CountOnlyRatingsForThatTargetAndType()
    {
        var alice = await SeedUserAsync();
        var bob = await SeedUserAsync();

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = NewSut(dbContext);
        await sut.RateAsync(alice, RatingTargetType.Series, 42, 42, 9);
        await sut.RateAsync(bob, RatingTargetType.Series, 43, 43, 1);        // another series
        await sut.RateAsync(bob, RatingTargetType.Movie, 42, 42, 2);         // same id, different kind
        await sut.RateAsync(alice, RatingTargetType.Episode, 42, 7, 3);      // same id, different kind

        var summary = await sut.GetSummaryAsync(RatingTargetType.Series, 42);

        summary.Should().Be(new RatingSummary(9, 1));
    }

    [Test]
    public async Task GetSummaryAsync_Should_ReflectAChangedAndARemovedRating()
    {
        var alice = await SeedUserAsync();
        var bob = await SeedUserAsync();

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = NewSut(dbContext);
        await sut.RateAsync(alice, RatingTargetType.Movie, 5, 5, 4);
        await sut.RateAsync(bob, RatingTargetType.Movie, 5, 5, 6);

        await sut.RateAsync(alice, RatingTargetType.Movie, 5, 5, 10);
        (await sut.GetSummaryAsync(RatingTargetType.Movie, 5)).Should().Be(new RatingSummary(8, 2));

        await sut.RemoveRatingAsync(bob, RatingTargetType.Movie, 5);
        (await sut.GetSummaryAsync(RatingTargetType.Movie, 5)).Should().Be(new RatingSummary(10, 1));
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
