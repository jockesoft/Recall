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

namespace Recall.Tests.Pages;

/// <summary>
/// The Details pages are public, so their POST handlers guard themselves. One
/// page stands in for the three: they share <c>TryGetUserId</c> and the same
/// <c>SignInRequired</c> shape.
/// </summary>
[TestFixture]
public class DetailsSignInGuardTests
{
    private const int MovieId = 287533;

    private Mock<ICurrentUserService> _currentUser = null!;
    private Mock<IMovieTrackingService> _movieTracking = null!;
    private Mock<ILikeRepository> _likes = null!;
    private Mock<IRatingRepository> _ratings = null!;
    private DetailsModel _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _currentUser = new Mock<ICurrentUserService>();
        _movieTracking = new Mock<IMovieTrackingService>();
        _likes = new Mock<ILikeRepository>();
        _ratings = new Mock<IRatingRepository>();

        _sut = new DetailsModel(
            Mock.Of<ITheTvDbService>(),
            _currentUser.Object,
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
    }

    private void AssertSentBackWithSignInToast(IActionResult result, string toDoWhat)
    {
        var redirect = result.Should().BeOfType<RedirectToPageResult>().Subject;
        redirect.RouteValues!["id"].Should().Be(MovieId);
        _sut.TempData["Toast.Error"].Should().Be($"You need to be signed in to {toDoWhat}.");
    }

    [Test]
    public async Task Anonymous_ToggleWatched_Should_ChangeNothing()
    {
        var result = await _sut.OnPostToggleMovieWatchedAsync(MovieId, CancellationToken.None);

        AssertSentBackWithSignInToast(result, "track watched movies");
        _movieTracking.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Anonymous_ToggleWatchlist_Should_ChangeNothing()
    {
        var result = await _sut.OnPostToggleWatchlistAsync(MovieId, CancellationToken.None);

        AssertSentBackWithSignInToast(result, "use your watchlist");
        _movieTracking.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Anonymous_LikeAndRate_Should_ChangeNothing()
    {
        AssertSentBackWithSignInToast(await _sut.OnPostToggleMovieLikeAsync(MovieId, CancellationToken.None), "like a movie");
        AssertSentBackWithSignInToast(await _sut.OnPostRateMovieAsync(MovieId, 8, CancellationToken.None), "rate a movie");
        AssertSentBackWithSignInToast(await _sut.OnPostClearMovieRatingAsync(MovieId, CancellationToken.None), "rate a movie");

        _likes.VerifyNoOtherCalls();
        _ratings.VerifyNoOtherCalls();
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
