using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.Import;

namespace Recall.Tests.Services.Import;

[TestFixture]
public sealed class WatchlistImportServiceTests
{
    private Mock<IWatchlistImportRepository> _importRepository = null!;
    private Mock<ITheTvDbService> _theTvDbService = null!;
    private Mock<ITrackedSeriesRepository> _trackedSeriesRepository = null!;
    private Mock<IMovieWatchRepository> _movieWatchRepository = null!;
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
        _movieWatchRepository = new Mock<IMovieWatchRepository>();
        _likeRepository = new Mock<ILikeRepository>();
        _ratingRepository = new Mock<IRatingRepository>();

        _sut = new WatchlistImportService(
            _importRepository.Object,
            _theTvDbService.Object,
            _trackedSeriesRepository.Object,
            _movieWatchRepository.Object,
            _likeRepository.Object,
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

    [Test]
    public async Task ProcessNextBatchAsync_Should_MarkWatchedAndRate_ForRatedUnwatchedMovie()
    {
        var item = Item(yourRating: 8);
        SetUpBatch(item);

        _theTvDbService
            .Setup(x => x.ResolveByRemoteIdAsync(item.ImdbId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteIdMatch(555, "A Movie", IsMovie: true));

        _movieWatchRepository
            .Setup(x => x.GetWatchedUtcAsync(UserId, 555, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime?)null);

        await _sut.ProcessNextBatchAsync(10);

        _ratingRepository.Verify(x =>
            x.RateAsync(UserId, RatingTargetType.Movie, 555, 555, 8, It.IsAny<CancellationToken>()), Times.Once);
        _movieWatchRepository.Verify(x => x.ToggleAsync(UserId, 555, It.IsAny<CancellationToken>()), Times.Once);
        _importRepository.Verify(x => x.MarkItemResultAsync(
            item.Id, WatchlistImportItemStatus.Imported, 555, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _importRepository.Verify(x => x.RecalculateJobProgressAsync(item.JobId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessNextBatchAsync_Should_NotReToggleWatched_ForRatedAlreadyWatchedMovie()
    {
        var item = Item(yourRating: 9);
        SetUpBatch(item);

        _theTvDbService
            .Setup(x => x.ResolveByRemoteIdAsync(item.ImdbId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteIdMatch(555, "A Movie", IsMovie: true));

        _movieWatchRepository
            .Setup(x => x.GetWatchedUtcAsync(UserId, 555, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTime.UtcNow.AddDays(-1));

        await _sut.ProcessNextBatchAsync(10);

        _ratingRepository.Verify(x =>
            x.RateAsync(UserId, RatingTargetType.Movie, 555, 555, 9, It.IsAny<CancellationToken>()), Times.Once);
        _movieWatchRepository.Verify(x => x.ToggleAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _importRepository.Verify(x => x.MarkItemResultAsync(
            item.Id, WatchlistImportItemStatus.Imported, 555, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessNextBatchAsync_Should_LikeUnratedMovie_WhenNotAlreadyLiked()
    {
        var item = Item(yourRating: null);
        SetUpBatch(item);

        _theTvDbService
            .Setup(x => x.ResolveByRemoteIdAsync(item.ImdbId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteIdMatch(777, "A Movie", IsMovie: true));

        _likeRepository
            .Setup(x => x.IsLikedAsync(UserId, LikeTargetType.Movie, 777, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _sut.ProcessNextBatchAsync(10);

        _ratingRepository.Verify(x => x.RateAsync(
            It.IsAny<Guid>(), It.IsAny<RatingTargetType>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never, "an unrated row must never invent a rating");
        _movieWatchRepository.Verify(x => x.ToggleAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _likeRepository.Verify(x => x.ToggleAsync(UserId, LikeTargetType.Movie, 777, 777, It.IsAny<CancellationToken>()), Times.Once);
        _importRepository.Verify(x => x.MarkItemResultAsync(
            item.Id, WatchlistImportItemStatus.Imported, 777, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessNextBatchAsync_Should_ReportAlreadyInLibrary_ForUnratedAlreadyLikedMovie()
    {
        var item = Item(yourRating: null);
        SetUpBatch(item);

        _theTvDbService
            .Setup(x => x.ResolveByRemoteIdAsync(item.ImdbId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteIdMatch(777, "A Movie", IsMovie: true));

        _likeRepository
            .Setup(x => x.IsLikedAsync(UserId, LikeTargetType.Movie, 777, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _sut.ProcessNextBatchAsync(10);

        _likeRepository.Verify(x => x.ToggleAsync(It.IsAny<Guid>(), It.IsAny<LikeTargetType>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never, "toggling an existing like would un-like it");
        _importRepository.Verify(x => x.MarkItemResultAsync(
            item.Id, WatchlistImportItemStatus.AlreadyInLibrary, 777, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
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

        await _sut.ProcessNextBatchAsync(10);

        _trackedSeriesRepository.Verify(x => x.AddAsync(
            It.Is<TrackedSeries>(s => s.UserId == UserId && s.TvdbId == 42), It.IsAny<CancellationToken>()), Times.Once);
        _ratingRepository.Verify(x =>
            x.RateAsync(UserId, RatingTargetType.Series, 42, 42, 7, It.IsAny<CancellationToken>()), Times.Once);
        _importRepository.Verify(x => x.MarkItemResultAsync(
            item.Id, WatchlistImportItemStatus.Imported, 42, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
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
