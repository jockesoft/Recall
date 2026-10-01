using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages.Series;
using Recall.Web.Services;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Pages;

/// <summary>
/// The POST handlers on Series/Details: the page is public, so each guards
/// itself; each validates its input; and each reports back through a toast.
/// </summary>
[TestFixture]
public class SeriesDetailsPostTests
{
    private const int SeriesId = 42;
    private const int EpisodeId = 4201;
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateTime BatchStamp = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private Mock<ITheTvDbService> _tvDb = null!;
    private Mock<ICurrentUserService> _currentUser = null!;
    private Mock<ITrackedSeriesRepository> _tracked = null!;
    private Mock<IWatchProgressService> _progress = null!;
    private Mock<ILikeRepository> _likes = null!;
    private Mock<IRatingRepository> _ratings = null!;
    private DetailsModel _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _tvDb = new Mock<ITheTvDbService>();
        _currentUser = new Mock<ICurrentUserService>();
        _tracked = new Mock<ITrackedSeriesRepository>();
        _progress = new Mock<IWatchProgressService>();
        _likes = new Mock<ILikeRepository>();
        _ratings = new Mock<IRatingRepository>();

        _sut = new DetailsModel(
            _tvDb.Object,
            _currentUser.Object,
            _tracked.Object,
            Mock.Of<IEpisodeWatchRepository>(),
            _progress.Object,
            _likes.Object,
            _ratings.Object,
            Mock.Of<IOmdbSnapshotStore>(),
            TimeProvider.System,
            NullLogger<DetailsModel>.Instance).WithTempData();

        _sut.Season = 2;
    }

    private void SignIn()
    {
        _currentUser.SetupGet(x => x.IsAuthenticated).Returns(true);
        _currentUser.SetupGet(x => x.UserId).Returns(UserId);
    }

    /// <summary>The series is already in the user's library, so "make sure it's tracked" is a no-op.</summary>
    private void AlreadyInLibrary() =>
        _tracked
            .Setup(x => x.GetByUserAndTvdbIdAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TrackedSeries { Id = Guid.NewGuid(), UserId = UserId, TvdbId = SeriesId, Name = "Show" });

    private static void AssertRedirectsBackToTheSeason(IActionResult result)
    {
        var redirect = result.Should().BeOfType<RedirectToPageResult>().Subject;
        redirect.RouteValues!["id"].Should().Be(SeriesId);
        redirect.RouteValues["season"].Should().Be(2);
    }

    // ---- sign-in guard ---------------------------------------------------------

    private static IEnumerable<TestCaseData> GuardedHandlers()
    {
        yield return new TestCaseData((Func<DetailsModel, Task<IActionResult>>)(m => m.OnPostToggleLibraryAsync(SeriesId, default)), "manage your library").SetName("Guard_ToggleLibrary");
        yield return new TestCaseData((Func<DetailsModel, Task<IActionResult>>)(m => m.OnPostToggleSeriesLikeAsync(SeriesId, default)), "like a series").SetName("Guard_ToggleSeriesLike");
        yield return new TestCaseData((Func<DetailsModel, Task<IActionResult>>)(m => m.OnPostRateSeriesAsync(SeriesId, 8, default)), "rate a series").SetName("Guard_RateSeries");
        yield return new TestCaseData((Func<DetailsModel, Task<IActionResult>>)(m => m.OnPostClearSeriesRatingAsync(SeriesId, default)), "rate a series").SetName("Guard_ClearSeriesRating");
        yield return new TestCaseData((Func<DetailsModel, Task<IActionResult>>)(m => m.OnPostToggleEpisodeWatchedAsync(SeriesId, EpisodeId, default)), "track watched episodes").SetName("Guard_ToggleEpisodeWatched");
        yield return new TestCaseData((Func<DetailsModel, Task<IActionResult>>)(m => m.OnPostMarkWatchedThroughAsync(SeriesId, EpisodeId, default)), "track watched episodes").SetName("Guard_MarkWatchedThrough");
        yield return new TestCaseData((Func<DetailsModel, Task<IActionResult>>)(m => m.OnPostMarkSeasonWatchedAsync(SeriesId, default)), "track watched episodes").SetName("Guard_MarkSeasonWatched");
        yield return new TestCaseData((Func<DetailsModel, Task<IActionResult>>)(m => m.OnPostMarkSeasonUnwatchedAsync(SeriesId, default)), "track watched episodes").SetName("Guard_MarkSeasonUnwatched");
        yield return new TestCaseData((Func<DetailsModel, Task<IActionResult>>)(m => m.OnPostUndoWatchedAsync(SeriesId, BatchStamp.Ticks, null, default)), "track watched episodes").SetName("Guard_UndoWatched");
    }

    [TestCaseSource(nameof(GuardedHandlers))]
    public async Task Anonymous_Should_BeSentBack_WithASignInToast_AndNothingChanged(
        Func<DetailsModel, Task<IActionResult>> handler, string toDoWhat)
    {
        var result = await handler(_sut);

        AssertRedirectsBackToTheSeason(result);
        _sut.ErrorToast().Should().Be($"You need to be signed in to {toDoWhat}.");
        _sut.SuccessToast().Should().BeNull();
        _tracked.VerifyNoOtherCalls();
        _progress.VerifyNoOtherCalls();
        _likes.VerifyNoOtherCalls();
        _ratings.VerifyNoOtherCalls();
    }

    [Test]
    public async Task CheckPriorEpisodes_Should_Return401_ForAnAnonymousCaller()
    {
        (await _sut.OnPostCheckPriorEpisodesAsync(SeriesId, EpisodeId, default)).Should().BeOfType<UnauthorizedResult>();
        _progress.VerifyNoOtherCalls();
    }

    // ---- library ---------------------------------------------------------------

    [Test]
    public async Task ToggleLibrary_Should_AddTheSeries_WhenItIsNotTracked()
    {
        SignIn();
        _tvDb
            .Setup(x => x.GetSeriesByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TvSeriesDetails(SeriesId, "Show", "show", "Overview", "https://img/p.jpg", "2020-01-01", 8.5, "Ended"));
        _tracked
            .Setup(x => x.AddAsync(It.IsAny<TrackedSeries>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _sut.OnPostToggleLibraryAsync(SeriesId, default);

        AssertRedirectsBackToTheSeason(result);
        _tracked.Verify(x => x.AddAsync(
            It.Is<TrackedSeries>(s => s.UserId == UserId && s.TvdbId == SeriesId && s.Name == "Show"), It.IsAny<CancellationToken>()), Times.Once);
        _sut.SuccessToast().Should().Be("Series saved to your library.");
    }

    [Test]
    public async Task ToggleLibrary_Should_SayAlreadyThere_WhenAConcurrentRequestAddedItFirst()
    {
        SignIn();
        _tvDb
            .Setup(x => x.GetSeriesByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TvSeriesDetails(SeriesId, "Show", "show", null, null, null, null, "Ended"));
        _tracked
            .Setup(x => x.AddAsync(It.IsAny<TrackedSeries>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _sut.OnPostToggleLibraryAsync(SeriesId, default);

        _sut.InfoToast().Should().Be("Series is already in your library.");
        _sut.ErrorToast().Should().BeNull();
    }

    [Test]
    public async Task ToggleLibrary_Should_RemoveTheSeries_WhenItIsTracked()
    {
        SignIn();
        var trackedId = Guid.NewGuid();
        _tracked
            .Setup(x => x.GetByUserAndTvdbIdAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TrackedSeries { Id = trackedId, UserId = UserId, TvdbId = SeriesId, Name = "Show" });

        await _sut.OnPostToggleLibraryAsync(SeriesId, default);

        _tracked.Verify(x => x.RemoveAsync(UserId, trackedId, It.IsAny<CancellationToken>()), Times.Once);
        _sut.InfoToast().Should().Be("Series removed from your library.");
    }

    [Test]
    public async Task ToggleLibrary_Should_ShowAnErrorToast_WhenTheRepositoryThrows()
    {
        SignIn();
        _tracked
            .Setup(x => x.GetByUserAndTvdbIdAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var result = await _sut.OnPostToggleLibraryAsync(SeriesId, default);

        AssertRedirectsBackToTheSeason(result);
        _sut.ErrorToast().Should().Be("Could not update your library right now.");
    }

    // ---- like and rating ---------------------------------------------------------

    [Test]
    public async Task ToggleSeriesLike_Should_ToggleTheLike_ForTheSignedInUser()
    {
        SignIn();

        var result = await _sut.OnPostToggleSeriesLikeAsync(SeriesId, default);

        AssertRedirectsBackToTheSeason(result);
        _likes.Verify(x => x.ToggleAsync(UserId, LikeTargetType.Series, SeriesId, SeriesId, It.IsAny<CancellationToken>()), Times.Once);
        _sut.ErrorToast().Should().BeNull();
    }

    [Test]
    public async Task ToggleSeriesLike_Should_ShowAnErrorToast_WhenTheRepositoryThrows()
    {
        SignIn();
        _likes
            .Setup(x => x.ToggleAsync(UserId, LikeTargetType.Series, SeriesId, SeriesId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        await _sut.OnPostToggleSeriesLikeAsync(SeriesId, default);

        _sut.ErrorToast().Should().Be("Could not update your like right now.");
    }

    [TestCase(1)]
    [TestCase(10)]
    public async Task RateSeries_Should_SaveARatingInRange(int value)
    {
        SignIn();

        var result = await _sut.OnPostRateSeriesAsync(SeriesId, value, default);

        AssertRedirectsBackToTheSeason(result);
        _ratings.Verify(x => x.RateAsync(UserId, RatingTargetType.Series, SeriesId, SeriesId, value, It.IsAny<CancellationToken>()), Times.Once);
        _sut.SuccessToast().Should().Be("Rating saved.");
    }

    [TestCase(0)]
    [TestCase(11)]
    [TestCase(-3)]
    public async Task RateSeries_Should_IgnoreARatingOutOfRange(int value)
    {
        SignIn();

        var result = await _sut.OnPostRateSeriesAsync(SeriesId, value, default);

        AssertRedirectsBackToTheSeason(result);
        _ratings.VerifyNoOtherCalls();
        _sut.SuccessToast().Should().BeNull();
    }

    [Test]
    public async Task RateSeries_Should_ShowAnErrorToast_WhenTheRepositoryThrows()
    {
        SignIn();
        _ratings
            .Setup(x => x.RateAsync(UserId, RatingTargetType.Series, SeriesId, SeriesId, 7, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        await _sut.OnPostRateSeriesAsync(SeriesId, 7, default);

        _sut.ErrorToast().Should().Be("Could not save your rating right now.");
        _sut.SuccessToast().Should().BeNull();
    }

    [Test]
    public async Task ClearSeriesRating_Should_RemoveTheRating()
    {
        SignIn();

        await _sut.OnPostClearSeriesRatingAsync(SeriesId, default);

        _ratings.Verify(x => x.RemoveRatingAsync(UserId, RatingTargetType.Series, SeriesId, It.IsAny<CancellationToken>()), Times.Once);
        _sut.InfoToast().Should().Be("Rating removed.");
    }

    // ---- prior-episodes check (JSON) -------------------------------------------

    [Test]
    public async Task CheckPriorEpisodes_Should_ReturnTheCountAsJson()
    {
        SignIn();
        _progress
            .Setup(x => x.GetPriorUnwatchedCountAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var result = await _sut.OnPostCheckPriorEpisodesAsync(SeriesId, EpisodeId, default);

        var json = result.Should().BeOfType<JsonResult>().Subject;
        System.Text.Json.JsonSerializer.Serialize(json.Value).Should().Be("""{"priorUnwatchedCount":3}""");
    }

    [TestCase(0)]
    [TestCase(-1)]
    public async Task CheckPriorEpisodes_Should_Return400_ForAnInvalidEpisodeId(int episodeId)
    {
        SignIn();

        (await _sut.OnPostCheckPriorEpisodesAsync(SeriesId, episodeId, default)).Should().BeOfType<BadRequestResult>();
    }

    [Test]
    public async Task CheckPriorEpisodes_Should_Return500_WhenTheServiceThrows()
    {
        SignIn();
        _progress
            .Setup(x => x.GetPriorUnwatchedCountAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _sut.OnPostCheckPriorEpisodesAsync(SeriesId, EpisodeId, default);

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(500);
    }

    // ---- single episode ---------------------------------------------------------

    [Test]
    public async Task ToggleEpisodeWatched_Should_MarkWatched_AndMakeSureTheSeriesIsInTheLibrary()
    {
        SignIn();
        AlreadyInLibrary();
        _progress
            .Setup(x => x.ToggleEpisodeWatchedAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(EpisodeWatchOutcome.MarkedWatched);

        var result = await _sut.OnPostToggleEpisodeWatchedAsync(SeriesId, EpisodeId, default);

        AssertRedirectsBackToTheSeason(result);
        _sut.SuccessToast().Should().Be("Episode marked as watched.");
        _tracked.Verify(x => x.GetByUserAndTvdbIdAsync(UserId, SeriesId, It.IsAny<CancellationToken>()), Times.Once);
        _tracked.Verify(x => x.RemoveAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never,
            "watching an episode of a tracked series must never un-track it");
    }

    [TestCase(EpisodeWatchOutcome.MarkedUnwatched, "info", "Episode marked as not watched.")]
    [TestCase(EpisodeWatchOutcome.NotAired, "error", "You can't mark an episode as watched before it has aired.")]
    [TestCase(EpisodeWatchOutcome.EpisodeNotInSeries, "error", "That episode doesn't belong to this series.")]
    public async Task ToggleEpisodeWatched_Should_ReportTheOutcome_WithoutTouchingTheLibrary(
        EpisodeWatchOutcome outcome, string kind, string expected)
    {
        SignIn();
        _progress
            .Setup(x => x.ToggleEpisodeWatchedAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        await _sut.OnPostToggleEpisodeWatchedAsync(SeriesId, EpisodeId, default);

        (kind == "info" ? _sut.InfoToast() : _sut.ErrorToast()).Should().Be(expected);
        _sut.SuccessToast().Should().BeNull();
        _tracked.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ToggleEpisodeWatched_Should_DoNothing_ForAnInvalidEpisodeId()
    {
        SignIn();

        var result = await _sut.OnPostToggleEpisodeWatchedAsync(SeriesId, 0, default);

        AssertRedirectsBackToTheSeason(result);
        _progress.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ToggleEpisodeWatched_Should_ShowAnErrorToast_WhenTheServiceThrows()
    {
        SignIn();
        _progress
            .Setup(x => x.ToggleEpisodeWatchedAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        await _sut.OnPostToggleEpisodeWatchedAsync(SeriesId, EpisodeId, default);

        _sut.ErrorToast().Should().Be("Could not update watched status right now.");
    }

    // ---- bulk marks and undo -----------------------------------------------------

    [Test]
    public async Task MarkWatchedThrough_Should_ReportTheCount_AndOfferAnUndo()
    {
        SignIn();
        AlreadyInLibrary();
        _progress
            .Setup(x => x.MarkWatchedThroughAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarkWatchedThroughResult(EpisodeFound: true, MarkedCount: 5, Batch: new WatchedBatch(4, BatchStamp)));

        var result = await _sut.OnPostMarkWatchedThroughAsync(SeriesId, EpisodeId, default);

        AssertRedirectsBackToTheSeason(result);
        _sut.SuccessToast().Should().Be("Marked 5 episodes as watched.");
        _sut.TempData[PageModelToastExtensions.UndoWatchedSeriesKey].Should().Be("42");
        _sut.TempData[PageModelToastExtensions.UndoWatchedStampKey].Should().Be(BatchStamp.Ticks.ToString());
    }

    [Test]
    public async Task MarkWatchedThrough_Should_SayEpisodeMarked_WhenOnlyOneWasCovered()
    {
        SignIn();
        AlreadyInLibrary();
        _progress
            .Setup(x => x.MarkWatchedThroughAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarkWatchedThroughResult(EpisodeFound: true, MarkedCount: 1, Batch: new WatchedBatch(1, BatchStamp)));

        await _sut.OnPostMarkWatchedThroughAsync(SeriesId, EpisodeId, default);

        _sut.SuccessToast().Should().Be("Episode marked as watched.");
        _sut.TempData.ContainsKey(PageModelToastExtensions.UndoWatchedStampKey).Should().BeFalse("a single episode is undone by clicking its tick");
    }

    [Test]
    public async Task MarkWatchedThrough_Should_ExplainARefusal_WithoutTouchingTheLibrary()
    {
        SignIn();
        _progress
            .SetupSequence(x => x.MarkWatchedThroughAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarkWatchedThroughResult(EpisodeFound: false, MarkedCount: 0))
            .ReturnsAsync(new MarkWatchedThroughResult(EpisodeFound: true, MarkedCount: 0, HasAired: false));

        await _sut.OnPostMarkWatchedThroughAsync(SeriesId, EpisodeId, default);
        _sut.ErrorToast().Should().Be("That episode doesn't belong to this series.");

        await _sut.OnPostMarkWatchedThroughAsync(SeriesId, EpisodeId, default);
        _sut.ErrorToast().Should().Be("You can't mark an episode as watched before it has aired.");

        _tracked.VerifyNoOtherCalls();
    }

    [Test]
    public async Task MarkSeasonWatched_Should_ReportHowManyWereNewlyMarked_AndOfferAnUndo()
    {
        SignIn();
        AlreadyInLibrary();
        _progress
            .Setup(x => x.MarkSeasonWatchedAsync(UserId, SeriesId, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeasonWatchResult(SeasonFound: true, new WatchedBatch(7, BatchStamp)));

        var result = await _sut.OnPostMarkSeasonWatchedAsync(SeriesId, default);

        AssertRedirectsBackToTheSeason(result);
        _sut.SuccessToast().Should().Be("Marked 7 episodes as watched.");
        _sut.TempData[PageModelToastExtensions.UndoWatchedStampKey].Should().Be(BatchStamp.Ticks.ToString());
    }

    [Test]
    public async Task MarkSeasonWatched_Should_SayNothingToMark_WhenTheSeasonIsAlreadyWatched()
    {
        SignIn();
        _progress
            .Setup(x => x.MarkSeasonWatchedAsync(UserId, SeriesId, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeasonWatchResult(SeasonFound: true, WatchedBatch.Empty));

        await _sut.OnPostMarkSeasonWatchedAsync(SeriesId, default);

        _sut.InfoToast().Should().Be("Nothing to mark — every aired episode in this season is already watched.");
        _sut.SuccessToast().Should().BeNull();
    }

    [Test]
    public async Task MarkSeasonWatched_Should_RejectASeasonTheSeriesDoesNotHave()
    {
        SignIn();
        _progress
            .Setup(x => x.MarkSeasonWatchedAsync(UserId, SeriesId, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeasonWatchResult(SeasonFound: false, WatchedBatch.Empty));

        await _sut.OnPostMarkSeasonWatchedAsync(SeriesId, default);

        _sut.ErrorToast().Should().Be("That season doesn't belong to this series.");
    }

    [Test]
    public async Task SeasonHandlers_Should_DoNothing_WhenNoSeasonWasPosted()
    {
        SignIn();
        _sut.Season = null;

        (await _sut.OnPostMarkSeasonWatchedAsync(SeriesId, default)).Should().BeOfType<RedirectToPageResult>();
        (await _sut.OnPostMarkSeasonUnwatchedAsync(SeriesId, default)).Should().BeOfType<RedirectToPageResult>();

        _progress.VerifyNoOtherCalls();
    }

    [TestCase(1, "Marked 1 episode as not watched.")]
    [TestCase(6, "Marked 6 episodes as not watched.")]
    public async Task MarkSeasonUnwatched_Should_ReportHowManyWereCleared(int removed, string expected)
    {
        SignIn();
        _progress
            .Setup(x => x.MarkSeasonUnwatchedAsync(UserId, SeriesId, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(removed);

        await _sut.OnPostMarkSeasonUnwatchedAsync(SeriesId, default);

        _sut.InfoToast().Should().Be(expected);
    }

    [TestCase(0, "Nothing left to undo.")]
    [TestCase(1, "Undone — 1 episode marked as not watched.")]
    [TestCase(4, "Undone — 4 episodes marked as not watched.")]
    public async Task UndoWatched_Should_ReverseTheBatch_AndReportIt(int removed, string expected)
    {
        SignIn();
        _progress
            .Setup(x => x.UndoWatchedBatchAsync(UserId, SeriesId, BatchStamp, It.IsAny<CancellationToken>()))
            .ReturnsAsync(removed);

        var result = await _sut.OnPostUndoWatchedAsync(SeriesId, BatchStamp.Ticks, returnUrl: null, default);

        AssertRedirectsBackToTheSeason(result);
        _sut.InfoToast().Should().Be(expected);
    }

    [TestCase(0L)]
    [TestCase(-5L)]
    [TestCase(long.MaxValue)]
    public async Task UndoWatched_Should_IgnoreAStampThatIsNotADate(long stamp)
    {
        SignIn();

        var result = await _sut.OnPostUndoWatchedAsync(SeriesId, stamp, returnUrl: null, default);

        result.Should().BeOfType<RedirectToPageResult>();
        _progress.VerifyNoOtherCalls();
    }

    [Test]
    public async Task UndoWatched_Should_GoBackToALocalReturnUrl_ButNeverToAnotherSite()
    {
        SignIn();
        var url = new Mock<IUrlHelper>();
        url.Setup(x => x.IsLocalUrl("/Episodes/Details/4201")).Returns(true);
        url.Setup(x => x.IsLocalUrl("https://evil.example/")).Returns(false);
        _sut.Url = url.Object;

        var local = await _sut.OnPostUndoWatchedAsync(SeriesId, BatchStamp.Ticks, "/Episodes/Details/4201", default);
        var foreign = await _sut.OnPostUndoWatchedAsync(SeriesId, BatchStamp.Ticks, "https://evil.example/", default);

        local.Should().BeOfType<LocalRedirectResult>().Which.Url.Should().Be("/Episodes/Details/4201");
        AssertRedirectsBackToTheSeason(foreign);
    }
}
