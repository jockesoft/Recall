using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages.Movies;
using Recall.Web.Services;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Pages;

/// <summary>
/// Signed-in behavior of the Movies/Details POST handlers. The anonymous side
/// is in <see cref="DetailsSignInGuardTests"/>.
/// </summary>
[TestFixture]
public class MovieDetailsPostTests
{
    private const int MovieId = 287533;
    private static readonly Guid UserId = Guid.NewGuid();

    private Mock<IMovieTrackingService> _tracking = null!;
    private Mock<ILikeRepository> _likes = null!;
    private Mock<IRatingRepository> _ratings = null!;
    private DetailsModel _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _tracking = new Mock<IMovieTrackingService>();
        _likes = new Mock<ILikeRepository>();
        _ratings = new Mock<IRatingRepository>();

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.IsAuthenticated).Returns(true);
        currentUser.SetupGet(x => x.UserId).Returns(UserId);

        _sut = new DetailsModel(
            Mock.Of<ITheTvDbService>(),
            currentUser.Object,
            _likes.Object,
            Mock.Of<IMovieWatchRepository>(),
            _tracking.Object,
            _ratings.Object,
            Mock.Of<IMovieOmdbSnapshotStore>(),
            TimeProvider.System,
            NullLogger<DetailsModel>.Instance).WithTempData();
    }

    private static void AssertRedirectsBackToTheMovie(IActionResult result) =>
        result.Should().BeOfType<RedirectToPageResult>().Which.RouteValues!["id"].Should().Be(MovieId);

    // ---- watched -----------------------------------------------------------------

    [TestCase(true, "success", "Movie marked as watched.")]
    [TestCase(false, "info", "Movie marked as not watched.")]
    public async Task ToggleMovieWatched_Should_ReportTheNewState(bool nowWatched, string kind, string expected)
    {
        _tracking
            .Setup(x => x.ToggleWatchedAsync(UserId, MovieId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(nowWatched);

        var result = await _sut.OnPostToggleMovieWatchedAsync(MovieId, default);

        AssertRedirectsBackToTheMovie(result);
        (kind == "success" ? _sut.SuccessToast() : _sut.InfoToast()).Should().Be(expected);
    }

    [Test]
    public async Task ToggleMovieWatched_Should_ShowAnErrorToast_WhenTheServiceThrows()
    {
        _tracking
            .Setup(x => x.ToggleWatchedAsync(UserId, MovieId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        await _sut.OnPostToggleMovieWatchedAsync(MovieId, default);

        _sut.ErrorToast().Should().Be("Could not update watched status right now.");
    }

    // ---- watchlist ---------------------------------------------------------------

    [Test]
    public async Task ToggleWatchlist_Should_RemoveTheMovie_WhenItWasOnTheWatchlist()
    {
        _tracking
            .Setup(x => x.RemoveFromWatchlistAsync(UserId, MovieId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _sut.OnPostToggleWatchlistAsync(MovieId, default);

        AssertRedirectsBackToTheMovie(result);
        _sut.InfoToast().Should().Be("Removed from your watchlist.");
        _tracking.Verify(
            x => x.AddToWatchlistAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestCase(MovieWatchlistOutcome.Added, "success", "Added to your watchlist.")]
    [TestCase(MovieWatchlistOutcome.AlreadyOnWatchlist, "success", "Added to your watchlist.")]
    [TestCase(MovieWatchlistOutcome.AlreadyWatched, "info", "You've already watched this movie.")]
    [TestCase(MovieWatchlistOutcome.MovieNotFound, "error", "Could not find that movie on TheTVDB.")]
    public async Task ToggleWatchlist_Should_AddTheMovie_AndReportTheOutcome(
        MovieWatchlistOutcome outcome, string kind, string expected)
    {
        _tracking
            .Setup(x => x.AddToWatchlistAsync(UserId, MovieId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        await _sut.OnPostToggleWatchlistAsync(MovieId, default);

        var toast = kind switch { "success" => _sut.SuccessToast(), "info" => _sut.InfoToast(), _ => _sut.ErrorToast() };
        toast.Should().Be(expected);
    }

    [Test]
    public async Task ToggleWatchlist_Should_Be404_ForAnInvalidId_AndShowAnErrorToast_WhenTheServiceThrows()
    {
        (await _sut.OnPostToggleWatchlistAsync(0, default)).Should().BeOfType<NotFoundResult>();

        _tracking
            .Setup(x => x.RemoveFromWatchlistAsync(UserId, MovieId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        await _sut.OnPostToggleWatchlistAsync(MovieId, default);

        _sut.ErrorToast().Should().Be("Could not update your watchlist right now.");
    }

    // ---- like and rating -----------------------------------------------------------

    [Test]
    public async Task ToggleMovieLike_Should_ToggleTheLike_OrSayItCouldNot()
    {
        var result = await _sut.OnPostToggleMovieLikeAsync(MovieId, default);

        AssertRedirectsBackToTheMovie(result);
        _likes.Verify(x => x.ToggleAsync(UserId, LikeTargetType.Movie, MovieId, MovieId, It.IsAny<CancellationToken>()), Times.Once);
        _sut.ErrorToast().Should().BeNull();

        _likes
            .Setup(x => x.ToggleAsync(UserId, LikeTargetType.Movie, MovieId, MovieId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        await _sut.OnPostToggleMovieLikeAsync(MovieId, default);
        _sut.ErrorToast().Should().Be("Could not update your like right now.");
    }

    [TestCase(1)]
    [TestCase(10)]
    public async Task RateMovie_Should_SaveARatingInRange(int value)
    {
        await _sut.OnPostRateMovieAsync(MovieId, value, default);

        _ratings.Verify(x => x.RateAsync(UserId, RatingTargetType.Movie, MovieId, MovieId, value, It.IsAny<CancellationToken>()), Times.Once);
        _sut.SuccessToast().Should().Be("Rating saved.");
    }

    [TestCase(0)]
    [TestCase(11)]
    public async Task RateMovie_Should_IgnoreARatingOutOfRange(int value)
    {
        var result = await _sut.OnPostRateMovieAsync(MovieId, value, default);

        AssertRedirectsBackToTheMovie(result);
        _ratings.VerifyNoOtherCalls();
        _sut.SuccessToast().Should().BeNull();
    }

    [Test]
    public async Task RateMovie_Should_ShowAnErrorToast_WhenTheRepositoryThrows()
    {
        _ratings
            .Setup(x => x.RateAsync(UserId, RatingTargetType.Movie, MovieId, MovieId, 6, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        await _sut.OnPostRateMovieAsync(MovieId, 6, default);

        _sut.ErrorToast().Should().Be("Could not save your rating right now.");
    }

    [Test]
    public async Task ClearMovieRating_Should_RemoveTheRating_OrSayItCouldNot()
    {
        await _sut.OnPostClearMovieRatingAsync(MovieId, default);
        _ratings.Verify(x => x.RemoveRatingAsync(UserId, RatingTargetType.Movie, MovieId, It.IsAny<CancellationToken>()), Times.Once);
        _sut.InfoToast().Should().Be("Rating removed.");

        _ratings
            .Setup(x => x.RemoveRatingAsync(UserId, RatingTargetType.Movie, MovieId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        await _sut.OnPostClearMovieRatingAsync(MovieId, default);
        _sut.ErrorToast().Should().Be("Could not update your rating right now.");
    }
}
