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
/// <see cref="MovieTrackingService"/> over the real watchlist and watch
/// repositories (in-memory SQLite): what matters is the rows left behind —
/// a movie is on the watchlist or watched, never both.
/// </summary>
[TestFixture]
public sealed class MovieTrackingServiceTests
{
    private const int MovieId = 287533;

    private SqliteConnection _connection = null!;
    private DbContextOptions<AppDbContext> _dbOptions = null!;
    private AppDbContext _dbContext = null!;
    private Mock<ITheTvDbService> _tvDbService = null!;
    private MovieTrackingService _sut = null!;
    private Guid _userId;

    [SetUp]
    public async Task SetUpAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new AppDbContext(_dbOptions);
        await _dbContext.Database.EnsureCreatedAsync();

        _userId = Guid.NewGuid();
        _dbContext.AppUsers.Add(new AppUserEntity
        {
            Id = _userId,
            Username = $"user-{_userId:N}",
            Email = $"{_userId:N}@test.local"
        });
        await _dbContext.SaveChangesAsync();

        _tvDbService = new Mock<ITheTvDbService>();
        _tvDbService
            .Setup(x => x.GetMovieAggregateByIdAsync(MovieId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MovieAggregate { TvdbId = MovieId, Name = "Oppenheimer" });

        _sut = new MovieTrackingService(
            new TrackedMovieRepository(_dbContext, NullLogger<TrackedMovieRepository>.Instance),
            new MovieWatchRepository(_dbContext, NullLogger<MovieWatchRepository>.Instance),
            _tvDbService.Object);
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        await _dbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<(bool OnWatchlist, bool Watched)> StateAsync()
    {
        await using var read = new AppDbContext(_dbOptions);
        return (
            await read.TrackedMovies.AnyAsync(x => x.UserId == _userId && x.TvdbId == MovieId),
            await read.UserMovieWatches.AnyAsync(x => x.UserId == _userId && x.MovieTvdbId == MovieId));
    }

    [Test]
    public async Task AddToWatchlistAsync_Should_LookTheMovieUp_AndStoreItsName()
    {
        var outcome = await _sut.AddToWatchlistAsync(_userId, MovieId);

        outcome.Should().Be(MovieWatchlistOutcome.Added);
        (await _sut.IsOnWatchlistAsync(_userId, MovieId)).Should().BeTrue();

        await using var read = new AppDbContext(_dbOptions);
        (await read.TrackedMovies.SingleAsync()).Name.Should().Be("Oppenheimer");
    }

    [Test]
    public async Task AddToWatchlistAsync_Should_SkipTheLookup_WhenTheCallerAlreadyKnowsTheName()
    {
        var outcome = await _sut.AddToWatchlistAsync(_userId, MovieId, knownName: "From the import");

        outcome.Should().Be(MovieWatchlistOutcome.Added);
        _tvDbService.VerifyNoOtherCalls();

        await using var read = new AppDbContext(_dbOptions);
        (await read.TrackedMovies.SingleAsync()).Name.Should().Be("From the import");
    }

    [Test]
    public async Task AddToWatchlistAsync_Should_AddNothing_ForAMovieTheTvDbDoesNotHave()
    {
        var outcome = await _sut.AddToWatchlistAsync(_userId, movieTvdbId: 404);

        outcome.Should().Be(MovieWatchlistOutcome.MovieNotFound);

        await using var read = new AppDbContext(_dbOptions);
        (await read.TrackedMovies.AnyAsync()).Should().BeFalse();
    }

    [Test]
    public async Task AddToWatchlistAsync_Should_ReportAlreadyOnWatchlist_TheSecondTime()
    {
        await _sut.AddToWatchlistAsync(_userId, MovieId);

        (await _sut.AddToWatchlistAsync(_userId, MovieId)).Should().Be(MovieWatchlistOutcome.AlreadyOnWatchlist);
    }

    [Test]
    public async Task AddToWatchlistAsync_Should_Refuse_AMovieAlreadyWatched()
    {
        await _sut.ToggleWatchedAsync(_userId, MovieId);

        var outcome = await _sut.AddToWatchlistAsync(_userId, MovieId);

        outcome.Should().Be(MovieWatchlistOutcome.AlreadyWatched);
        (await StateAsync()).Should().Be((false, true));
    }

    [Test]
    public async Task ToggleWatchedAsync_Should_TakeTheMovieOffTheWatchlist_WhenItBecomesWatched()
    {
        await _sut.AddToWatchlistAsync(_userId, MovieId);

        (await _sut.ToggleWatchedAsync(_userId, MovieId)).Should().BeTrue();

        (await StateAsync()).Should().Be((false, true));
    }

    [Test]
    public async Task ToggleWatchedAsync_Should_NotPutTheMovieBack_WhenItIsUnwatchedAgain()
    {
        await _sut.AddToWatchlistAsync(_userId, MovieId);
        await _sut.ToggleWatchedAsync(_userId, MovieId);

        (await _sut.ToggleWatchedAsync(_userId, MovieId)).Should().BeFalse();

        (await StateAsync()).Should().Be((false, false));
    }

    [Test]
    public async Task MarkWatchedAsync_Should_WatchOnce_AndNeverUnwatch()
    {
        await _sut.AddToWatchlistAsync(_userId, MovieId);

        (await _sut.MarkWatchedAsync(_userId, MovieId, WatchSource.Import)).Should().BeFalse("it was not watched before this call");
        (await _sut.MarkWatchedAsync(_userId, MovieId, WatchSource.Import)).Should().BeTrue("a second call must report it, not toggle it back");

        (await StateAsync()).Should().Be((false, true));
    }

    [Test]
    public async Task ToggleWatchedAsync_Should_RecordASingleWatch_AndMarkWatchedAsyncTheSourceItIsGiven()
    {
        await _sut.ToggleWatchedAsync(_userId, MovieId);
        await _sut.MarkWatchedAsync(_userId, MovieId + 1, WatchSource.Import);

        var sources = await _dbContext.UserMovieWatches.AsNoTracking()
            .ToDictionaryAsync(x => x.MovieTvdbId, x => x.Source);
        sources[MovieId].Should().Be(WatchSource.Single, "the user pressed 'Mark as watched'");
        sources[MovieId + 1].Should().Be(WatchSource.Import);
    }

    [Test]
    public async Task RemoveFromWatchlistAsync_Should_ReportWhetherAnythingWasRemoved()
    {
        await _sut.AddToWatchlistAsync(_userId, MovieId);

        (await _sut.RemoveFromWatchlistAsync(_userId, MovieId)).Should().BeTrue();
        (await _sut.RemoveFromWatchlistAsync(_userId, MovieId)).Should().BeFalse();
    }
}
