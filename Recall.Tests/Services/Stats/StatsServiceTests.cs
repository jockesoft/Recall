using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.Stats;

namespace Recall.Tests.Services.Stats;

[TestFixture]
public sealed class StatsServiceTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private Mock<IEpisodeWatchRepository> _episodeWatches = null!;
    private Mock<IMovieWatchRepository> _movieWatches = null!;
    private Mock<ITrackedSeriesRepository> _tracked = null!;
    private Mock<IRatingRepository> _ratings = null!;
    private Mock<ITheTvDbService> _tvDb = null!;
    private StatsService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _episodeWatches = new Mock<IEpisodeWatchRepository>();
        _movieWatches = new Mock<IMovieWatchRepository>();
        _tracked = new Mock<ITrackedSeriesRepository>();
        _ratings = new Mock<IRatingRepository>();

        // Strict: any call that is not one of the two cache-only reads fails the test.
        _tvDb = new Mock<ITheTvDbService>(MockBehavior.Strict);

        Watched([], []);
        _ratings.Setup(x => x.GetValueCountsAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, int>());
        _tracked.Setup(x => x.GetByUserAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        _sut = new StatsService(
            _episodeWatches.Object,
            _movieWatches.Object,
            _tracked.Object,
            _ratings.Object,
            _tvDb.Object,
            new FixedTimeProvider(Now),
            NullLogger<StatsService>.Instance);
    }

    private void Watched(EpisodeWatchRecord[] episodes, MovieWatch[] movies)
    {
        _episodeWatches.Setup(x => x.GetWatchesAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync(episodes);
        _movieWatches.Setup(x => x.GetWatchedMoviesAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync(movies);
    }

    private static EpisodeWatchRecord Episode(int seriesId, int episodeId) =>
        new(seriesId, episodeId, Now.AddDays(-3), WatchSource.Single);

    private static SeriesAggregate Series(int id, int runtime) => new()
    {
        TvdbId = id,
        Name = $"Show {id}",
        AverageRuntimeMinutes = runtime,
        Episodes = []
    };

    [Test]
    public async Task GetAsync_Should_ReadMetadataFromTheCachesOnly_OncePerTitle()
    {
        Watched(
            [Episode(1, 11), Episode(1, 12), Episode(2, 21)],
            [new MovieWatch(900, Now.AddDays(-1), WatchSource.Single)]);
        _tvDb.Setup(x => x.GetCachedSeriesAggregateAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Series(1, 40));
        _tvDb.Setup(x => x.GetCachedSeriesAggregateAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(Series(2, 20));
        _tvDb.Setup(x => x.GetCachedMovieAggregateAsync(900, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MovieAggregate { TvdbId = 900, Name = "Film", RuntimeMinutes = 100 });

        var stats = await _sut.GetAsync(UserId);

        stats.Totals.Should().Be(new StatsTotals(40 + 40 + 20 + 100, 3, 1, 0));
        stats.Months[^1].Month.Should().Be(new DateOnly(2026, 10, 1), "the chart ends with the current UTC month");
        stats.Months[^1].Episodes.Should().Be(3);

        _tvDb.Verify(x => x.GetCachedSeriesAggregateAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        _tvDb.Verify(x => x.GetCachedSeriesAggregateAsync(2, It.IsAny<CancellationToken>()), Times.Once);
        _tvDb.Verify(x => x.GetCachedMovieAggregateAsync(900, It.IsAny<CancellationToken>()), Times.Once);
        _tvDb.VerifyNoOtherCalls();
    }

    [Test]
    public async Task GetAsync_Should_CountATitleThatIsNotCached_WithoutFetchingIt()
    {
        Watched([Episode(1, 11)], [new MovieWatch(900, Now, WatchSource.Import)]);
        _tvDb.Setup(x => x.GetCachedSeriesAggregateAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync((SeriesAggregate?)null);
        _tvDb.Setup(x => x.GetCachedMovieAggregateAsync(900, It.IsAny<CancellationToken>())).ReturnsAsync((MovieAggregate?)null);

        var stats = await _sut.GetAsync(UserId);

        stats.Totals.Should().Be(new StatsTotals(0, 1, 1, 0));
        stats.Gaps.TitlesNotCached.Should().Be(2);

        // The mock is strict: a fall-back to GetSeriesAggregateByIdAsync or
        // GetMovieAggregateByIdAsync (which can reach TheTVDB) would have thrown.
        _tvDb.Verify(x => x.GetSeriesAggregateByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _tvDb.Verify(x => x.GetMovieAggregateByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task GetAsync_Should_TreatAFailedCacheRead_AsNotCached_AndKeepGoing()
    {
        Watched([Episode(1, 11), Episode(2, 21)], []);
        _tvDb.Setup(x => x.GetCachedSeriesAggregateAsync(1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Redis is away"));
        _tvDb.Setup(x => x.GetCachedSeriesAggregateAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(Series(2, 20));

        var stats = await _sut.GetAsync(UserId);

        stats.Totals.Should().Be(new StatsTotals(20, 2, 0, 0));
        stats.Gaps.TitlesNotCached.Should().Be(1);
    }

    [Test]
    public async Task GetAsync_Should_CountAFinishedSeries_FromTheUsersLibrary()
    {
        Watched([Episode(1, 11)], []);
        _tracked.Setup(x => x.GetByUserAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TrackedSeries { TvdbId = 1, UserId = UserId, Name = "Show 1" }]);
        _tvDb.Setup(x => x.GetCachedSeriesAggregateAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Series(1, 40) with
            {
                Status = new SeriesStatus { Name = "Ended" },
                Episodes = [new EpisodeSummary { Id = 11, SeasonNumber = 1, EpisodeNumber = 1, Aired = new DateOnly(2020, 1, 1) }]
            });

        (await _sut.GetAsync(UserId)).Totals.SeriesFinished.Should().Be(1);
    }

    [Test]
    public async Task GetAsync_Should_ReturnEmpty_WithoutTouchingTheCaches_ForAUserWithNothing()
    {
        var stats = await _sut.GetAsync(UserId);

        stats.Should().BeSameAs(UserStats.Empty);
        _tvDb.VerifyNoOtherCalls();
        _tracked.Verify(x => x.GetByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task GetAsync_Should_GiveTheRatings_ToAUserWhoHasOnlyRated()
    {
        _ratings.Setup(x => x.GetValueCountsAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, int> { [8] = 2 });

        var stats = await _sut.GetAsync(UserId);

        stats.Ratings.Count.Should().Be(2);
        stats.Ratings.Average.Should().Be(8);
        stats.IsEmpty.Should().BeFalse();
    }
}
