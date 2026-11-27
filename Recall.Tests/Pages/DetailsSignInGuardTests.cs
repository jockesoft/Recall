using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages.Movies;
using Recall.Web.Services;
using Recall.Web.Services.WatchTracking;
using Recall.Web.Services.PendingActions;
using Recall.Tests.TestSupport;

namespace Recall.Tests.Pages;

/// <summary>
/// The Details pages are public, so their POST handlers guard themselves. A
/// visitor's press of an action is kept as a pending action and sends them to
/// sign in, changing nothing yet; anything else gets the sign-in toast. Movie
/// Details here; Series and Episode Details in their POST tests.
/// </summary>
[TestFixture]
public class DetailsSignInGuardTests
{
    private const int MovieId = 287533;

    private Mock<ICurrentUserService> _currentUser = null!;
    private Mock<IMovieTrackingService> _movieTracking = null!;
    private Mock<ILikeRepository> _likes = null!;
    private Mock<IRatingRepository> _ratings = null!;
    private Mock<IPendingActions> _pending = null!;
    private DetailsModel _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _currentUser = new Mock<ICurrentUserService>();
        _movieTracking = new Mock<IMovieTrackingService>();
        _likes = new Mock<ILikeRepository>();
        _ratings = new Mock<IRatingRepository>();
        _pending = new Mock<IPendingActions>();

        _sut = new DetailsModel(
            Mock.Of<ITheTvDbService>(),
            _currentUser.Object,
            _pending.Object,
            _likes.Object,
            Mock.Of<IMovieWatchRepository>(),
            _movieTracking.Object,
            _ratings.Object,
            Mock.Of<IMovieOmdbSnapshotStore>(),
            TimeProvider.System,
            NullLogger<DetailsModel>.Instance)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>())
        };
        _sut.WithHttpContext();
    }

    /// <summary>The visitor's press was kept, exactly, and they were sent to sign in, returning to this movie.</summary>
    private void AssertKeptAndSentToSignIn(IActionResult result, PendingAction expected)
    {
        var redirect = result.Should().BeOfType<RedirectToPageResult>().Subject;
        redirect.PageName.Should().Be("/Account/Login");
        redirect.RouteValues!["returnUrl"].Should().Be("/Movies/Details/287533");
        redirect.RouteValues.Should().HaveCount(1, "nothing about the action travels in the address");
        _pending.Verify(x => x.Start(It.IsAny<HttpContext>(), expected), Times.Once);
        _sut.TempData.Should().BeEmpty("no toast: the sign-in page says what will happen");
    }

    private void AssertSentBackWithSignInToast(IActionResult result, string toDoWhat)
    {
        var redirect = result.Should().BeOfType<RedirectToPageResult>().Subject;
        redirect.RouteValues!["id"].Should().Be(MovieId);
        _sut.TempData["Toast.Error"].Should().Be($"You need to be signed in to {toDoWhat}.");
    }

    [Test]
    public async Task Anonymous_ToggleWatched_Should_KeepMarkWatched_AndChangeNothing()
    {
        var result = await _sut.OnPostToggleMovieWatchedAsync(MovieId, CancellationToken.None);

        AssertKeptAndSentToSignIn(result, new PendingAction(PendingActionKind.MarkMovieWatched, MovieId));
        _movieTracking.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Anonymous_ToggleWatchlist_Should_KeepAddToWatchlist_AndChangeNothing()
    {
        var result = await _sut.OnPostToggleWatchlistAsync(MovieId, CancellationToken.None);

        AssertKeptAndSentToSignIn(result, new PendingAction(PendingActionKind.AddMovieToWatchlist, MovieId));
        _movieTracking.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Anonymous_Like_Should_KeepALike_AndChangeNothing()
    {
        AssertKeptAndSentToSignIn(await _sut.OnPostToggleMovieLikeAsync(MovieId, CancellationToken.None),
            new PendingAction(PendingActionKind.LikeMovie, MovieId));

        _likes.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Anonymous_Rate_Should_KeepTheRating_AndChangeNothing()
    {
        AssertKeptAndSentToSignIn(await _sut.OnPostRateMovieAsync(MovieId, 8, CancellationToken.None),
            new PendingAction(PendingActionKind.RateMovie, MovieId, 8));

        _ratings.VerifyNoOtherCalls();
    }

    [TestCase(0)]
    [TestCase(11)]
    public async Task Anonymous_RateOutOfRange_Should_KeepNothing(int value)
    {
        var result = await _sut.OnPostRateMovieAsync(MovieId, value, CancellationToken.None);

        result.Should().BeOfType<RedirectToPageResult>().Which.PageName.Should().BeNull("back to this page");
        _pending.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Anonymous_ClearRating_Should_GetTheSignInToast_AndKeepNothing()
    {
        // A visitor has no rating to clear, so this is not one of the actions that are kept.
        AssertSentBackWithSignInToast(await _sut.OnPostClearMovieRatingAsync(MovieId, CancellationToken.None), "rate a movie");

        _pending.VerifyNoOtherCalls();
        _ratings.VerifyNoOtherCalls();
    }

    [TestCase(true, "Took the movie off your watchlist again.")]
    [TestCase(false, "Nothing left to undo.")]
    public async Task UndoWatchlist_Should_TakeTheMovieOffAgain(bool wasOn, string toast)
    {
        var userId = Guid.NewGuid();
        _currentUser.SetupGet(x => x.IsAuthenticated).Returns(true);
        _currentUser.SetupGet(x => x.UserId).Returns(userId);
        _movieTracking.Setup(x => x.RemoveFromWatchlistAsync(userId, MovieId, It.IsAny<CancellationToken>())).ReturnsAsync(wasOn);

        var result = await _sut.OnPostUndoWatchlistAsync(MovieId, CancellationToken.None);

        result.Should().BeOfType<RedirectToPageResult>().Which.RouteValues!["id"].Should().Be(MovieId);
        _sut.TempData["Toast.Info"].Should().Be(toast);
    }

    [Test]
    public async Task Anonymous_UndoWatchlist_Should_GetTheSignInToast()
    {
        AssertSentBackWithSignInToast(await _sut.OnPostUndoWatchlistAsync(MovieId, CancellationToken.None), "use your watchlist");
        _movieTracking.VerifyNoOtherCalls();
    }

    [Test]
    public async Task SignedIn_ToggleWatched_Should_ActForThatUser()
    {
        var userId = Guid.NewGuid();
        _currentUser.SetupGet(x => x.IsAuthenticated).Returns(true);
        _currentUser.SetupGet(x => x.UserId).Returns(userId);
        _movieTracking
            .Setup(x => x.ToggleWatchedAsync(userId, MovieId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _sut.OnPostToggleMovieWatchedAsync(MovieId, CancellationToken.None);

        result.Should().BeOfType<RedirectToPageResult>();
        _movieTracking.Verify(x => x.ToggleWatchedAsync(userId, MovieId, It.IsAny<CancellationToken>()), Times.Once);
        _sut.TempData["Toast.Success"].Should().Be("Movie marked as watched.");
    }
}
