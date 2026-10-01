using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.Import;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Services.Import;

[TestFixture]
public sealed class WatchlistImportServiceTests
{
    private Mock<IWatchlistImportRepository> _importRepository = null!;
    private Mock<ITheTvDbService> _theTvDbService = null!;
    private Mock<ITrackedSeriesRepository> _trackedSeriesRepository = null!;
    private Mock<IMovieTrackingService> _movieTrackingService = null!;
    private Mock<ILikeRepository> _likeRepository = null!;
    private Mock<IRatingRepository> _ratingRepository = null!;
    private WatchlistImportService _sut = null!;

    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [SetUp]
    public void SetUp()
    {
        _importRepository = new Mock<IWatchlistImportRepository>();
        _theTvDbService = new Mock<ITheTvDbService>();
        _trackedSeriesRepository = new Mock<ITrackedSeriesRepository>();
        _movieTrackingService = new Mock<IMovieTrackingService>();
        // Not a dependency any more — kept only to prove no like is ever created.
        _likeRepository = new Mock<ILikeRepository>();
        _ratingRepository = new Mock<IRatingRepository>();

        _sut = new WatchlistImportService(
            _importRepository.Object,
            _theTvDbService.Object,
            _trackedSeriesRepository.Object,
            _movieTrackingService.Object,
            _ratingRepository.Object,
            NullLogger<WatchlistImportService>.Instance);
    }

    private static WatchlistImportItem Item(int? yourRating, Guid jobId = default, string imdbId = "tt0000001") =>
        new(
            Guid.NewGuid(),
            jobId == default ? Guid.NewGuid() : jobId,
            UserId,
            RowNumber: 1,
            ImdbId: imdbId,
            Title: "Some Title",
            TitleType: "irrelevant-for-processing",
            YourRating: yourRating,
            Status: WatchlistImportItemStatus.Pending,
            ResolvedTvdbId: null,
            ResultMessage: null,
            CreatedUtc: DateTime.UtcNow,
            ProcessedUtc: null);

    private void SetUpBatch(params WatchlistImportItem[] items) =>
        _importRepository
            .Setup(x => x.ClaimNextPendingBatchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);

    private void SetUpMovieMatch(WatchlistImportItem item, int tvdbId, string? name = "A Movie") =>
        _theTvDbService
            .Setup(x => x.ResolveByRemoteIdAsync(item.ImdbId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteIdMatch(tvdbId, name, IsMovie: true));

    private void VerifyNeverRated() =>
        _ratingRepository.Verify(x => x.RateAsync(
                It.IsAny<Guid>(), It.IsAny<RatingTargetType>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never, "an unrated row must never invent a rating");

    [Test]
    public async Task ProcessNextBatchAsync_Should_MarkWatchedAndRate_ForRatedUnwatchedMovie()
    {
        var item = Item(yourRating: 8);
        SetUpBatch(item);
        SetUpMovieMatch(item, 555);
        _movieTrackingService
            .Setup(x => x.MarkWatchedAsync(UserId, 555, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _sut.ProcessNextBatchAsync(10);

        _ratingRepository.Verify(x =>
            x.RateAsync(UserId, RatingTargetType.Movie, 555, 555, 8, It.IsAny<CancellationToken>()), Times.Once);
        _movieTrackingService.Verify(x => x.MarkWatchedAsync(UserId, 555, It.IsAny<CancellationToken>()), Times.Once);
        _importRepository.Verify(x => x.MarkItemResultAsync(
            item.Id, WatchlistImportItemStatus.Imported, 555, "Marked watched and rated 8/10.", It.IsAny<CancellationToken>()), Times.Once);
        _importRepository.Verify(x => x.RecalculateJobProgressAsync(item.JobId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessNextBatchAsync_Should_OnlyUpdateTheRating_ForRatedAlreadyWatchedMovie()
    {
        var item = Item(yourRating: 9);
        SetUpBatch(item);
        SetUpMovieMatch(item, 555);
        _movieTrackingService
            .Setup(x => x.MarkWatchedAsync(UserId, 555, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _sut.ProcessNextBatchAsync(10);

        _ratingRepository.Verify(x =>
            x.RateAsync(UserId, RatingTargetType.Movie, 555, 555, 9, It.IsAny<CancellationToken>()), Times.Once);
        _movieTrackingService.Verify(
            x => x.ToggleWatchedAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never, "a toggle would un-watch a movie that was already watched");
        _importRepository.Verify(x => x.MarkItemResultAsync(
            item.Id, WatchlistImportItemStatus.Imported, 555, "Already watched — rating updated to 9/10.", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessNextBatchAsync_Should_PutAnUnratedMovieOnTheWatchlist_NotLikeIt()
    {
        var item = Item(yourRating: null);
        SetUpBatch(item);
        SetUpMovieMatch(item, 777);
        _movieTrackingService
            .Setup(x => x.AddToWatchlistAsync(UserId, 777, "A Movie", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MovieWatchlistOutcome.Added);

        await _sut.ProcessNextBatchAsync(10);

        VerifyNeverRated();
        _movieTrackingService.Verify(x => x.AddToWatchlistAsync(UserId, 777, "A Movie", It.IsAny<CancellationToken>()), Times.Once);
        _movieTrackingService.Verify(
            x => x.MarkWatchedAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _likeRepository.VerifyNoOtherCalls();
        _importRepository.Verify(x => x.MarkItemResultAsync(
            item.Id, WatchlistImportItemStatus.Imported, 777, "Added to your watchlist.", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessNextBatchAsync_Should_UseTheCsvTitle_WhenTheTvDbMatchHasNoName()
    {
        var item = Item(yourRating: null);
        SetUpBatch(item);
        SetUpMovieMatch(item, 777, name: null);
        _movieTrackingService
            .Setup(x => x.AddToWatchlistAsync(UserId, 777, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MovieWatchlistOutcome.Added);

        await _sut.ProcessNextBatchAsync(10);

        _movieTrackingService.Verify(
            x => x.AddToWatchlistAsync(UserId, 777, "Some Title", It.IsAny<CancellationToken>()), Times.Once,
            "a name is what spares the service a second TheTVDB lookup");
    }

    [TestCase(MovieWatchlistOutcome.AlreadyOnWatchlist, "Already on your watchlist.")]
    [TestCase(MovieWatchlistOutcome.AlreadyWatched, "Already watched.")]
    public async Task ProcessNextBatchAsync_Should_ReportAlreadyInLibrary_ForAnUnratedMovieTheUserAlreadyHas(
        MovieWatchlistOutcome outcome, string expectedMessage)
    {
        var item = Item(yourRating: null);
        SetUpBatch(item);
        SetUpMovieMatch(item, 777);
        _movieTrackingService
            .Setup(x => x.AddToWatchlistAsync(UserId, 777, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        await _sut.ProcessNextBatchAsync(10);

        VerifyNeverRated();
        _importRepository.Verify(x => x.MarkItemResultAsync(
            item.Id, WatchlistImportItemStatus.AlreadyInLibrary, 777, expectedMessage, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessNextBatchAsync_Should_AddSeriesToLibrary_WithoutTouchingEpisodes()
    {
        var item = Item(yourRating: 7);
        SetUpBatch(item);

        _theTvDbService
            .Setup(x => x.ResolveByRemoteIdAsync(item.ImdbId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteIdMatch(42, "A Series", IsMovie: false));

        _trackedSeriesRepository
            .Setup(x => x.GetByUserAndTvdbIdAsync(UserId, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TrackedSeries?)null);

        _theTvDbService
            .Setup(x => x.GetSeriesByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TvSeriesDetails(42, "A Series", "a-series", "Overview", "img.jpg", "2020-01-01", 8.5, "Ended"));

        _trackedSeriesRepository
            .Setup(x => x.AddAsync(It.IsAny<TrackedSeries>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _sut.ProcessNextBatchAsync(10);

        _trackedSeriesRepository.Verify(x => x.AddAsync(
            It.Is<TrackedSeries>(s => s.UserId == UserId && s.TvdbId == 42), It.IsAny<CancellationToken>()), Times.Once);
        _ratingRepository.Verify(x =>
            x.RateAsync(UserId, RatingTargetType.Series, 42, 42, 7, It.IsAny<CancellationToken>()), Times.Once);
        _importRepository.Verify(x => x.MarkItemResultAsync(
            item.Id, WatchlistImportItemStatus.Imported, 42, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessNextBatchAsync_Should_ReportAlreadyInLibrary_WhenTheSeriesWasAddedConcurrently()
    {
        var item = Item(yourRating: null);
        SetUpBatch(item);

        _theTvDbService
            .Setup(x => x.ResolveByRemoteIdAsync(item.ImdbId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteIdMatch(42, "A Series", IsMovie: false));
        _trackedSeriesRepository
            .Setup(x => x.GetByUserAndTvdbIdAsync(UserId, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TrackedSeries?)null);
        _theTvDbService
            .Setup(x => x.GetSeriesByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TvSeriesDetails(42, "A Series", "a-series", "Overview", "img.jpg", "2020-01-01", 8.5, "Ended"));

        // The repository reports "already there" as a result, not an exception.
        _trackedSeriesRepository
            .Setup(x => x.AddAsync(It.IsAny<TrackedSeries>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _sut.ProcessNextBatchAsync(10);

        _importRepository.Verify(x => x.MarkItemResultAsync(
            item.Id, WatchlistImportItemStatus.AlreadyInLibrary, 42, "Already in your library.", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessNextBatchAsync_Should_ReportAlreadyInLibrary_ForTrackedSeries()
    {
        var item = Item(yourRating: null);
        SetUpBatch(item);

        _theTvDbService
            .Setup(x => x.ResolveByRemoteIdAsync(item.ImdbId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteIdMatch(42, "A Series", IsMovie: false));

        _trackedSeriesRepository
            .Setup(x => x.GetByUserAndTvdbIdAsync(UserId, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TrackedSeries { Id = Guid.NewGuid(), UserId = UserId, TvdbId = 42, Name = "A Series" });

        await _sut.ProcessNextBatchAsync(10);

        _theTvDbService.Verify(x => x.GetSeriesByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never,
            "already-tracked series shouldn't need a details fetch");
        _trackedSeriesRepository.Verify(x => x.AddAsync(It.IsAny<TrackedSeries>(), It.IsAny<CancellationToken>()), Times.Never);
        _importRepository.Verify(x => x.MarkItemResultAsync(
            item.Id, WatchlistImportItemStatus.AlreadyInLibrary, 42, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessNextBatchAsync_Should_ReportNotFound_WhenTheTvDbHasNoMatch()
    {
        var item = Item(yourRating: 5);
        SetUpBatch(item);

        _theTvDbService
            .Setup(x => x.ResolveByRemoteIdAsync(item.ImdbId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RemoteIdMatch?)null);

        await _sut.ProcessNextBatchAsync(10);

        _importRepository.Verify(x => x.MarkItemResultAsync(
            item.Id, WatchlistImportItemStatus.NotFound, null, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _ratingRepository.Verify(x => x.RateAsync(
            It.IsAny<Guid>(), It.IsAny<RatingTargetType>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ProcessNextBatchAsync_Should_MarkFailed_AndKeepGoing_WhenOneItemThrows()
    {
        var failing = Item(yourRating: 5, imdbId: "tt1111111");
        var succeeding = Item(yourRating: null, jobId: failing.JobId, imdbId: "tt2222222");
        SetUpBatch(failing, succeeding);

        _theTvDbService
            .Setup(x => x.ResolveByRemoteIdAsync(failing.ImdbId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        _theTvDbService
            .Setup(x => x.ResolveByRemoteIdAsync(succeeding.ImdbId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteIdMatch(1, "Fine", IsMovie: true));

        _likeRepository
            .Setup(x => x.IsLikedAsync(UserId, LikeTargetType.Movie, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _sut.ProcessNextBatchAsync(10);

        _importRepository.Verify(x => x.MarkItemResultAsync(
            failing.Id, WatchlistImportItemStatus.Failed, null, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _importRepository.Verify(x => x.MarkItemResultAsync(
            succeeding.Id, WatchlistImportItemStatus.Imported, 1, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
