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
        yield return new TestCaseData((Func<DetailsModel, Task<IActionResult>>)(m => m.OnPostStopWatchingAsync(SeriesId, default)), "manage your library").SetName("Guard_StopWatching");
        yield return new TestCaseData((Func<DetailsModel, Task<IActionResult>>)(m => m.OnPostResumeWatchingAsync(SeriesId, false, null, default)), "manage your library").SetName("Guard_ResumeWatching");
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
    public async Task ToggleEpisodeWatched_Should_MarkWatched_AndLeaveTheLibraryToTheWatchService()
    {
        // The service adds an untracked series itself (MarkAddsToLibraryTests);
        // the page neither adds nor, ever, removes.
        SignIn();
        _progress
            .Setup(x => x.ToggleEpisodeWatchedAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(EpisodeWatchOutcome.MarkedWatched);

        var result = await _sut.OnPostToggleEpisodeWatchedAsync(SeriesId, EpisodeId, default);

        AssertRedirectsBackToTheSeason(result);
        _sut.SuccessToast().Should().Be("Episode marked as watched.");
        _tracked.VerifyNoOtherCalls();
    }

    [Test]
    public async Task AMarkThatAddedTheSeriesToTheLibrary_Should_SaySo_InTheSameToast()
    {
        SignIn();
        _progress
            .Setup(x => x.ToggleEpisodeWatchedAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EpisodeWatchResult(EpisodeWatchOutcome.MarkedWatched, AddedToLibrary: "Silo"));
        _progress
            .Setup(x => x.MarkWatchedThroughAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarkWatchedThroughResult(EpisodeFound: true, MarkedCount: 5, Batch: new WatchedBatch(5, BatchStamp), AddedToLibrary: "Silo"));
        _progress
            .Setup(x => x.MarkSeasonWatchedAsync(UserId, SeriesId, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeasonWatchResult(SeasonFound: true, new WatchedBatch(7, BatchStamp), UpToDate, AddedToLibrary: "Silo"));

        await _sut.OnPostToggleEpisodeWatchedAsync(SeriesId, EpisodeId, default);
        _sut.SuccessToast().Should().Be("Marked the episode as watched and added Silo to your library.");

        await _sut.OnPostMarkWatchedThroughAsync(SeriesId, EpisodeId, default);
        _sut.SuccessToast().Should().Be("Marked 5 episodes as watched and added Silo to your library.");
        _sut.TempData[PageModelToastExtensions.UndoWatchedStampKey].Should().Be(BatchStamp.Ticks.ToString());

        await _sut.OnPostMarkSeasonWatchedAsync(SeriesId, default);
        _sut.SuccessToast().Should().Be(
            "Marked 7 episodes as watched and added Silo to your library. You're up to date with Silo. We'll let you know when a new episode airs.");
        _sut.InfoToast().Should().BeNull("still one toast");
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

    // ---- "You're up to date" / "You've finished": one toast, on every way of marking ----

    private static readonly SeriesCaughtUp Finished = new(SeriesId, "Chernobyl", Finished: true, UserHasRated: false);
    private static readonly SeriesCaughtUp UpToDate = new(SeriesId, "Silo", Finished: false);

    [Test]
    public async Task ToggleEpisodeWatched_Should_SayFinished_InsteadOfEpisodeMarked_AndOfferRateIt()
    {
        // Also the header's "Mark S01E05 watched" button: it posts to the same handler.
        SignIn();
        AlreadyInLibrary();
        _progress
            .Setup(x => x.ToggleEpisodeWatchedAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EpisodeWatchResult(EpisodeWatchOutcome.MarkedWatched, Finished));

        await _sut.OnPostToggleEpisodeWatchedAsync(SeriesId, EpisodeId, default);

        _sut.SuccessToast().Should().Be("You've finished Chernobyl.");
        _sut.InfoToast().Should().BeNull("never two toasts for one action");
        _sut.TempData[PageModelToastExtensions.CaughtUpKindKey].Should().Be(PageModelToastExtensions.CaughtUpFinished);
        _sut.TempData[PageModelToastExtensions.RateSeriesKey].Should().Be(SeriesId.ToString());
    }

    [Test]
    public async Task ToggleEpisodeWatched_Should_SayUpToDate_ForASeriesThatContinues()
    {
        SignIn();
        AlreadyInLibrary();
        _progress
            .Setup(x => x.ToggleEpisodeWatchedAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EpisodeWatchResult(EpisodeWatchOutcome.MarkedWatched, UpToDate));

        await _sut.OnPostToggleEpisodeWatchedAsync(SeriesId, EpisodeId, default);

        _sut.SuccessToast().Should().Be("You're up to date with Silo. We'll let you know when a new episode airs.");
        _sut.TempData[PageModelToastExtensions.CaughtUpKindKey].Should().Be(PageModelToastExtensions.CaughtUpUpToDate);
        _sut.TempData.ContainsKey(PageModelToastExtensions.RateSeriesKey).Should().BeFalse();
    }

    [Test]
    public async Task MarkWatchedThrough_Should_AddFinishedToTheCount_AndKeepTheUndo()
    {
        SignIn();
        AlreadyInLibrary();
        _progress
            .Setup(x => x.MarkWatchedThroughAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarkWatchedThroughResult(EpisodeFound: true, MarkedCount: 8, Batch: new WatchedBatch(8, BatchStamp), CaughtUp: Finished));

        await _sut.OnPostMarkWatchedThroughAsync(SeriesId, EpisodeId, default);

        _sut.SuccessToast().Should().Be("Marked 8 episodes as watched. You've finished Chernobyl.");
        _sut.TempData[PageModelToastExtensions.UndoWatchedStampKey].Should().Be(BatchStamp.Ticks.ToString());
        _sut.TempData[PageModelToastExtensions.RateSeriesKey].Should().Be(SeriesId.ToString());
    }

    [Test]
    public async Task MarkSeasonWatched_Should_AddUpToDateToTheCount_AndKeepTheUndo()
    {
        SignIn();
        AlreadyInLibrary();
        _progress
            .Setup(x => x.MarkSeasonWatchedAsync(UserId, SeriesId, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeasonWatchResult(SeasonFound: true, new WatchedBatch(7, BatchStamp), UpToDate));

        await _sut.OnPostMarkSeasonWatchedAsync(SeriesId, default);

        _sut.SuccessToast().Should().Be(
            "Marked 7 episodes as watched. You're up to date with Silo. We'll let you know when a new episode airs.");
        _sut.TempData[PageModelToastExtensions.UndoWatchedStampKey].Should().Be(BatchStamp.Ticks.ToString());
        _sut.TempData[PageModelToastExtensions.CaughtUpKindKey].Should().Be(PageModelToastExtensions.CaughtUpUpToDate);
    }

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

    // ---- stop watching / resume watching ----------------------------------------------

    [Test]
    public async Task StopWatching_Should_StopTheSeries_AndOfferAnUndo_WithNoConfirmation()
    {
        SignIn();
        _progress
            .Setup(x => x.StopWatchingAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StopWatchingResult(StopWatchingOutcome.Stopped, "Silo"));

        var result = await _sut.OnPostStopWatchingAsync(SeriesId, default);

        AssertRedirectsBackToTheSeason(result);
        _sut.SuccessToast().Should().Be("Stopped watching Silo.");
        _sut.TempData[PageModelToastExtensions.UndoStoppedSeriesKey].Should().Be(SeriesId.ToString());
        _tracked.VerifyNoOtherCalls();
    }

    [Test]
    public async Task StopWatching_Should_SayWhy_WhenTheSeriesIsFinished_OrAlreadyStopped()
    {
        SignIn();
        _progress
            .SetupSequence(x => x.StopWatchingAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StopWatchingResult(StopWatchingOutcome.Finished, "Chernobyl"))
            .ReturnsAsync(new StopWatchingResult(StopWatchingOutcome.AlreadyStopped, "Silo"));

        await _sut.OnPostStopWatchingAsync(SeriesId, default);
        _sut.InfoToast().Should().Be("You've finished Chernobyl, so there is nothing to stop.");

        await _sut.OnPostStopWatchingAsync(SeriesId, default);
        _sut.InfoToast().Should().Be("You had already stopped watching Silo.");

        _sut.SuccessToast().Should().BeNull();
        _sut.TempData.ContainsKey(PageModelToastExtensions.UndoStoppedSeriesKey).Should().BeFalse();
    }

    [Test]
    public async Task StopWatching_Should_ShowAnErrorToast_WhenTheServiceThrows()
    {
        SignIn();
        _progress
            .Setup(x => x.StopWatchingAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database down"));

        var result = await _sut.OnPostStopWatchingAsync(SeriesId, default);

        AssertRedirectsBackToTheSeason(result);
        _sut.ErrorToast().Should().Be("Could not update your library right now.");
    }

    [Test]
    public async Task ResumeWatching_Should_ResumeTheSeries_WithAPlainSuccessToast()
    {
        SignIn();
        _progress
            .Setup(x => x.ResumeWatchingAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Silo");

        var result = await _sut.OnPostResumeWatchingAsync(SeriesId, undo: false, returnUrl: null, default);

        AssertRedirectsBackToTheSeason(result);
        _sut.SuccessToast().Should().Be("Resumed watching Silo.");
        _sut.TempData.ContainsKey(PageModelToastExtensions.UndoStoppedSeriesKey).Should().BeFalse("resuming has no Undo");
    }

    [Test]
    public async Task ResumeWatching_AsTheUndoOfAStop_Should_SayUndone_AndGoBackToWhereItWasPressed()
    {
        SignIn();
        var url = new Mock<IUrlHelper>();
        url.Setup(x => x.IsLocalUrl("/Dashboard")).Returns(true);
        url.Setup(x => x.IsLocalUrl("https://evil.example/")).Returns(false);
        _sut.Url = url.Object;
        _progress
            .Setup(x => x.ResumeWatchingAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Silo");

        var local = await _sut.OnPostResumeWatchingAsync(SeriesId, undo: true, returnUrl: "/Dashboard", default);

        local.Should().BeOfType<LocalRedirectResult>().Which.Url.Should().Be("/Dashboard");
        _sut.InfoToast().Should().Be("Undone — you're still watching Silo.");
        _sut.SuccessToast().Should().BeNull();

        var foreign = await _sut.OnPostResumeWatchingAsync(SeriesId, undo: true, returnUrl: "https://evil.example/", default);
        AssertRedirectsBackToTheSeason(foreign);
    }

    [Test]
    public async Task ResumeWatching_Should_SayNothingWasLeftToDo_WhenTheSeriesWasNotStopped()
    {
        SignIn();
        _progress
            .Setup(x => x.ResumeWatchingAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await _sut.OnPostResumeWatchingAsync(SeriesId, undo: false, returnUrl: null, default);
        _sut.InfoToast().Should().Be("You are already watching this series.");

        await _sut.OnPostResumeWatchingAsync(SeriesId, undo: true, returnUrl: null, default);
        _sut.InfoToast().Should().Be("Nothing left to undo.");
        _sut.SuccessToast().Should().BeNull();
    }

    [Test]
    public async Task ResumeWatching_Should_ShowAnErrorToast_WhenTheServiceThrows()
    {
        SignIn();
        _progress
            .Setup(x => x.ResumeWatchingAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database down"));

        await _sut.OnPostResumeWatchingAsync(SeriesId, undo: false, returnUrl: null, default);

        _sut.ErrorToast().Should().Be("Could not update your library right now.");
    }

    [Test]
    public async Task AMarkThatResumedAStoppedSeries_Should_SaySo_InTheSameToast()
    {
        SignIn();
        _progress
            .Setup(x => x.ToggleEpisodeWatchedAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EpisodeWatchResult(EpisodeWatchOutcome.MarkedWatched, ResumedWatching: "Silo"));
        _progress
            .Setup(x => x.MarkWatchedThroughAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarkWatchedThroughResult(EpisodeFound: true, MarkedCount: 5, Batch: new WatchedBatch(5, BatchStamp), ResumedWatching: "Silo"));
        _progress
            .Setup(x => x.MarkSeasonWatchedAsync(UserId, SeriesId, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeasonWatchResult(SeasonFound: true, new WatchedBatch(7, BatchStamp), UpToDate, ResumedWatching: "Silo"));

        await _sut.OnPostToggleEpisodeWatchedAsync(SeriesId, EpisodeId, default);
        _sut.SuccessToast().Should().Be("Marked the episode as watched and resumed watching Silo.");

        await _sut.OnPostMarkWatchedThroughAsync(SeriesId, EpisodeId, default);
        _sut.SuccessToast().Should().Be("Marked 5 episodes as watched and resumed watching Silo.");
        _sut.TempData[PageModelToastExtensions.UndoWatchedStampKey].Should().Be(BatchStamp.Ticks.ToString());

        await _sut.OnPostMarkSeasonWatchedAsync(SeriesId, default);
        _sut.SuccessToast().Should().Be(
            "Marked 7 episodes as watched and resumed watching Silo. You're up to date with Silo. We'll let you know when a new episode airs.");
        _sut.InfoToast().Should().BeNull("still one toast");
        _sut.TempData.ContainsKey(PageModelToastExtensions.UndoStoppedSeriesKey).Should().BeFalse("the Undo is the mark's, not a stop's");
    }

    [Test]
    public async Task AToggleThatResumedAndCaughtUp_Should_KeepBothSentences()
    {
        SignIn();
        _progress
            .Setup(x => x.ToggleEpisodeWatchedAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EpisodeWatchResult(EpisodeWatchOutcome.MarkedWatched, UpToDate, ResumedWatching: "Silo"));

        await _sut.OnPostToggleEpisodeWatchedAsync(SeriesId, EpisodeId, default);

        _sut.SuccessToast().Should().Be(
            "Marked the episode as watched and resumed watching Silo. You're up to date with Silo. We'll let you know when a new episode airs.");
    }

    [Test]
    public async Task Unmarking_Should_SayNothingAboutTheStoppedState()
    {
        SignIn();
        _progress
            .Setup(x => x.ToggleEpisodeWatchedAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(EpisodeWatchOutcome.MarkedUnwatched);

        await _sut.OnPostToggleEpisodeWatchedAsync(SeriesId, EpisodeId, default);

        _sut.InfoToast().Should().Be("Episode marked as not watched.");
        _progress.Verify(x => x.ResumeWatchingAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _progress.Verify(x => x.StopWatchingAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- undoing a mark that resumed a stopped series -----------------------------------

    private static readonly DateTime StoppedOn = new(2026, 9, 4, 18, 0, 0, DateTimeKind.Utc);

    private void VerifyRestored(Times times) =>
        _progress.Verify(
            x => x.RestoreStoppedAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), times);

    [Test]
    public async Task AMarkThatResumed_Should_PutTheStoppedDateInItsUndo()
    {
        SignIn();
        _progress
            .Setup(x => x.MarkWatchedThroughAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarkWatchedThroughResult(
                EpisodeFound: true, MarkedCount: 5, Batch: new WatchedBatch(5, BatchStamp), ResumedWatching: "Silo", ResumedFromStoppedUtc: StoppedOn));
        _progress
            .Setup(x => x.MarkSeasonWatchedAsync(UserId, SeriesId, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeasonWatchResult(
                SeasonFound: true, new WatchedBatch(7, BatchStamp), ResumedWatching: "Silo", ResumedFromStoppedUtc: StoppedOn));

        await _sut.OnPostMarkWatchedThroughAsync(SeriesId, EpisodeId, default);
        _sut.TempData[PageModelToastExtensions.UndoWatchedStoppedKey].Should().Be(StoppedOn.Ticks.ToString());

        _sut.TempData.Clear();
        await _sut.OnPostMarkSeasonWatchedAsync(SeriesId, default);
        _sut.TempData[PageModelToastExtensions.UndoWatchedStoppedKey].Should().Be(StoppedOn.Ticks.ToString());
    }

    [Test]
    public async Task UndoWatched_Should_StopTheSeriesAgain_WithItsOriginalDate_WhenTheMarkHadResumedIt()
    {
        SignIn();
        _progress
            .Setup(x => x.UndoWatchedBatchAsync(UserId, SeriesId, BatchStamp, It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        _progress
            .Setup(x => x.RestoreStoppedAsync(UserId, SeriesId, StoppedOn, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _sut.OnPostUndoWatchedAsync(SeriesId, BatchStamp.Ticks, null, default, stoppedStamp: StoppedOn.Ticks);

        AssertRedirectsBackToTheSeason(result);
        _sut.InfoToast().Should().Be("Undone — 3 episodes marked as not watched. You've stopped watching the series again.");
        _progress.Verify(x => x.RestoreStoppedAsync(UserId, SeriesId, StoppedOn, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task UndoWatched_Should_NotTouchTheStoppedState_ForAnOrdinaryMark()
    {
        SignIn();
        _progress
            .Setup(x => x.UndoWatchedBatchAsync(UserId, SeriesId, BatchStamp, It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        await _sut.OnPostUndoWatchedAsync(SeriesId, BatchStamp.Ticks, null, default);

        _sut.InfoToast().Should().Be("Undone — 3 episodes marked as not watched.");
        VerifyRestored(Times.Never());
    }

    [TestCase(0L)]
    [TestCase(-5L)]
    [TestCase(long.MaxValue)]
    public async Task UndoWatched_Should_IgnoreAStoppedStampThatIsNotADate(long stoppedStamp)
    {
        SignIn();
        _progress
            .Setup(x => x.UndoWatchedBatchAsync(UserId, SeriesId, BatchStamp, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        await _sut.OnPostUndoWatchedAsync(SeriesId, BatchStamp.Ticks, null, default, stoppedStamp);

        _sut.InfoToast().Should().Be("Undone — 1 episode marked as not watched.");
        VerifyRestored(Times.Never());
    }

    [Test]
    public async Task UndoWatched_Should_NotStopAgain_WhenNothingWasLeftToUndo_OrTheSeriesWasStoppedSince()
    {
        SignIn();
        _progress
            .SetupSequence(x => x.UndoWatchedBatchAsync(UserId, SeriesId, BatchStamp, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0)
            .ReturnsAsync(2);

        // A second press of the same Undo: nothing removed, so nothing restored.
        await _sut.OnPostUndoWatchedAsync(SeriesId, BatchStamp.Ticks, null, default, stoppedStamp: StoppedOn.Ticks);
        _sut.InfoToast().Should().Be("Nothing left to undo.");
        VerifyRestored(Times.Never());

        // The restore changed nothing (already stopped again): the toast does not claim it did.
        await _sut.OnPostUndoWatchedAsync(SeriesId, BatchStamp.Ticks, null, default, stoppedStamp: StoppedOn.Ticks);
        _sut.InfoToast().Should().Be("Undone — 2 episodes marked as not watched.");
    }

    [Test]
    public async Task PlainUnmarking_Should_NeverRestoreOrChangeTheStoppedState()
    {
        SignIn();
        _progress
            .Setup(x => x.ToggleEpisodeWatchedAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(EpisodeWatchOutcome.MarkedUnwatched);
        _progress
            .Setup(x => x.MarkSeasonUnwatchedAsync(UserId, SeriesId, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(4);

        await _sut.OnPostToggleEpisodeWatchedAsync(SeriesId, EpisodeId, default);
        await _sut.OnPostMarkSeasonUnwatchedAsync(SeriesId, default);

        VerifyRestored(Times.Never());
        _progress.Verify(x => x.StopWatchingAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _progress.Verify(x => x.ResumeWatchingAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
