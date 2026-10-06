using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.External.Omdb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages.Episodes;
using Recall.Web.Services;
using Recall.Web.Services.External.Omdb;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Pages;

[TestFixture]
public class EpisodeDetailsPostTests
{
    private const int EpisodeId = 4201;
    private const int SeriesId = 42;
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateTime BatchStamp = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private Mock<ITheTvDbService> _tvDb = null!;
    private Mock<ICurrentUserService> _currentUser = null!;
    private Mock<IWatchProgressService> _progress = null!;
    private Mock<ILikeRepository> _likes = null!;
    private Mock<IRatingRepository> _ratings = null!;
    private DetailsModel _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _tvDb = new Mock<ITheTvDbService>();
        _currentUser = new Mock<ICurrentUserService>();
        _progress = new Mock<IWatchProgressService>();
        _likes = new Mock<ILikeRepository>();
        _ratings = new Mock<IRatingRepository>();

        _sut = new DetailsModel(
            NullLogger<DetailsModel>.Instance,
            _tvDb.Object,
            _currentUser.Object,
            Mock.Of<IEpisodeWatchRepository>(),
            _progress.Object,
            _likes.Object,
            _ratings.Object,
            Mock.Of<IOmdbApiClient>(),
            Mock.Of<IEpisodeOmdbSnapshotStore>(),
            Mock.Of<IOmdbRequestBudget>(),
            Options.Create(new OmdbOptions()),
            TimeProvider.System).WithTempData();
    }

    private void SignIn()
    {
        _currentUser.SetupGet(x => x.IsAuthenticated).Returns(true);
        _currentUser.SetupGet(x => x.UserId).Returns(UserId);
    }

    private void EpisodeBelongsTo(int? seriesId) =>
        _tvDb
            .Setup(x => x.GetEpisodeDetailsAsync(EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Episode { Id = EpisodeId, SeriesId = seriesId, Name = "Pilot" });

    private static void AssertRedirectsBackToTheEpisode(IActionResult result) =>
        result.Should().BeOfType<RedirectToPageResult>().Which.RouteValues!["id"].Should().Be(EpisodeId);

    private void VerifyNothingWritten()
    {
        _progress.VerifyNoOtherCalls();
        _likes.VerifyNoOtherCalls();
        _ratings.VerifyNoOtherCalls();
    }

    // ---- sign-in guard and route validation ------------------------------------

    private static IEnumerable<TestCaseData> Handlers()
    {
        yield return new TestCaseData((Func<DetailsModel, int, Task<IActionResult>>)((m, id) => m.OnPostToggleLikeAsync(id, default)), "like an episode").SetName("{m}_ToggleLike");
        yield return new TestCaseData((Func<DetailsModel, int, Task<IActionResult>>)((m, id) => m.OnPostRateEpisodeAsync(id, 8, default)), "rate an episode").SetName("{m}_RateEpisode");
        yield return new TestCaseData((Func<DetailsModel, int, Task<IActionResult>>)((m, id) => m.OnPostClearEpisodeRatingAsync(id, default)), "rate an episode").SetName("{m}_ClearEpisodeRating");
        yield return new TestCaseData((Func<DetailsModel, int, Task<IActionResult>>)((m, id) => m.OnPostToggleWatchedAsync(id, default)), "track watched episodes").SetName("{m}_ToggleWatched");
        yield return new TestCaseData((Func<DetailsModel, int, Task<IActionResult>>)((m, id) => m.OnPostMarkWatchedThroughAsync(id, default)), "track watched episodes").SetName("{m}_MarkWatchedThrough");
    }

    [TestCaseSource(nameof(Handlers))]
    public async Task Anonymous_Should_BeSentBack_WithASignInToast(
        Func<DetailsModel, int, Task<IActionResult>> handler, string toDoWhat)
    {
        var result = await handler(_sut, EpisodeId);

        AssertRedirectsBackToTheEpisode(result);
        _sut.ErrorToast().Should().Be($"You need to be signed in to {toDoWhat}.");
        VerifyNothingWritten();
        _tvDb.VerifyNoOtherCalls();
    }

    [TestCaseSource(nameof(Handlers))]
    public async Task AnInvalidEpisodeId_Should_Be404(
        Func<DetailsModel, int, Task<IActionResult>> handler, string _)
    {
        SignIn();

        (await handler(_sut, 0)).Should().BeOfType<NotFoundResult>();
        VerifyNothingWritten();
    }

    // ---- like ------------------------------------------------------------------

    [Test]
    public async Task ToggleLike_Should_RecordTheLikeAgainstTheEpisodesSeries()
    {
        SignIn();
        EpisodeBelongsTo(SeriesId);

        var result = await _sut.OnPostToggleLikeAsync(EpisodeId, default);

        AssertRedirectsBackToTheEpisode(result);
        _likes.Verify(x => x.ToggleAsync(UserId, LikeTargetType.Episode, EpisodeId, SeriesId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestCase(null)]
    [TestCase(0)]
    public async Task ToggleLike_Should_Refuse_WhenTheEpisodeHasNoParentSeries(int? seriesId)
    {
        SignIn();
        EpisodeBelongsTo(seriesId);

        await _sut.OnPostToggleLikeAsync(EpisodeId, default);

        _sut.ErrorToast().Should().Be("Could not verify this episode against its series right now.");
        _likes.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ToggleLike_Should_ShowAnErrorToast_WhenTheLookupThrows()
    {
        SignIn();
        _tvDb
            .Setup(x => x.GetEpisodeDetailsAsync(EpisodeId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("TheTVDB is down"));

        await _sut.OnPostToggleLikeAsync(EpisodeId, default);

        _sut.ErrorToast().Should().Be("Could not update your like right now.");
    }

    // ---- rating ------------------------------------------------------------------

    [Test]
    public async Task RateEpisode_Should_SaveTheRatingAgainstTheEpisodesSeries()
    {
        SignIn();
        EpisodeBelongsTo(SeriesId);

        await _sut.OnPostRateEpisodeAsync(EpisodeId, 9, default);

        _ratings.Verify(x => x.RateAsync(UserId, RatingTargetType.Episode, EpisodeId, SeriesId, 9, It.IsAny<CancellationToken>()), Times.Once);
        _sut.SuccessToast().Should().Be("Rating saved.");
    }

    [TestCase(0)]
    [TestCase(11)]
    public async Task RateEpisode_Should_IgnoreARatingOutOfRange(int value)
    {
        SignIn();
        EpisodeBelongsTo(SeriesId);

        var result = await _sut.OnPostRateEpisodeAsync(EpisodeId, value, default);

        AssertRedirectsBackToTheEpisode(result);
        _ratings.VerifyNoOtherCalls();
    }

    [Test]
    public async Task RateEpisode_Should_Refuse_WhenTheEpisodeHasNoParentSeries()
    {
        SignIn();
        EpisodeBelongsTo(null);

        await _sut.OnPostRateEpisodeAsync(EpisodeId, 9, default);

        _sut.ErrorToast().Should().Be("Could not verify this episode against its series right now.");
        _ratings.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ClearEpisodeRating_Should_RemoveTheRating_OrSayItCouldNot()
    {
        SignIn();

        await _sut.OnPostClearEpisodeRatingAsync(EpisodeId, default);
        _ratings.Verify(x => x.RemoveRatingAsync(UserId, RatingTargetType.Episode, EpisodeId, It.IsAny<CancellationToken>()), Times.Once);
        _sut.InfoToast().Should().Be("Rating removed.");

        _ratings
            .Setup(x => x.RemoveRatingAsync(UserId, RatingTargetType.Episode, EpisodeId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        await _sut.OnPostClearEpisodeRatingAsync(EpisodeId, default);
        _sut.ErrorToast().Should().Be("Could not update your rating right now.");
    }

    // ---- watched -----------------------------------------------------------------

    [TestCase(EpisodeWatchOutcome.MarkedWatched, "success", "Episode marked as watched.")]
    [TestCase(EpisodeWatchOutcome.MarkedUnwatched, "info", "Episode marked as not watched.")]
    [TestCase(EpisodeWatchOutcome.NotAired, "error", "You can't mark an episode as watched before it has aired.")]
    [TestCase(EpisodeWatchOutcome.EpisodeNotInSeries, "error", "Could not verify this episode against its series right now.")]
    public async Task ToggleWatched_Should_UseTheSeriesFromTheEpisodeRecord_AndReportTheOutcome(
        EpisodeWatchOutcome outcome, string kind, string expected)
    {
        SignIn();
        EpisodeBelongsTo(SeriesId);
        _progress
            .Setup(x => x.ToggleEpisodeWatchedAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        var result = await _sut.OnPostToggleWatchedAsync(EpisodeId, default);

        AssertRedirectsBackToTheEpisode(result);
        var toast = kind switch { "success" => _sut.SuccessToast(), "info" => _sut.InfoToast(), _ => _sut.ErrorToast() };
        toast.Should().Be(expected);
    }

    [Test]
    public async Task ToggleWatched_Should_Be404_WhenTheEpisodeDoesNotExist()
    {
        SignIn();
        _tvDb
            .Setup(x => x.GetEpisodeDetailsAsync(EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Episode?)null);

        (await _sut.OnPostToggleWatchedAsync(EpisodeId, default)).Should().BeOfType<NotFoundResult>();
        _progress.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ToggleWatched_Should_Refuse_AnEpisodeWithoutASeries()
    {
        SignIn();
        EpisodeBelongsTo(null);

        await _sut.OnPostToggleWatchedAsync(EpisodeId, default);

        _sut.ErrorToast().Should().Be("Episode does not have a valid series reference.");
        _progress.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ToggleWatched_Should_ShowAnErrorToast_WhenTheServiceThrows()
    {
        SignIn();
        EpisodeBelongsTo(SeriesId);
        _progress
            .Setup(x => x.ToggleEpisodeWatchedAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        await _sut.OnPostToggleWatchedAsync(EpisodeId, default);

        _sut.ErrorToast().Should().Be("Could not update watched status right now.");
    }

    // ---- marking from here adds the series to the library, and says so -------------

    private void EpisodeIs(int season, int number) =>
        _tvDb
            .Setup(x => x.GetEpisodeDetailsAsync(EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Episode { Id = EpisodeId, SeriesId = SeriesId, SeasonNumber = season, Number = number, Name = "Machines" });

    [Test]
    public async Task ToggleWatched_Should_SayTheSeriesWasAddedToTheLibrary_WhenItWas()
    {
        SignIn();
        EpisodeIs(1, 3);
        _progress
            .Setup(x => x.ToggleEpisodeWatchedAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EpisodeWatchResult(EpisodeWatchOutcome.MarkedWatched, AddedToLibrary: "Silo"));

        await _sut.OnPostToggleWatchedAsync(EpisodeId, default);

        _sut.SuccessToast().Should().Be("Marked S01E03 as watched and added Silo to your library.");
        _sut.TempData.ContainsKey(PageModelToastExtensions.CaughtUpKindKey).Should().BeFalse();
    }

    [Test]
    public async Task ToggleWatched_Should_SayOnlyThatItWasMarked_WhenTheSeriesWasAlreadyInTheLibrary()
    {
        SignIn();
        EpisodeIs(1, 3);
        _progress
            .Setup(x => x.ToggleEpisodeWatchedAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EpisodeWatchResult(EpisodeWatchOutcome.MarkedWatched));

        await _sut.OnPostToggleWatchedAsync(EpisodeId, default);

        _sut.SuccessToast().Should().Be("Episode marked as watched.");
    }

    [Test]
    public async Task ToggleWatched_Should_CombineAddedToTheLibrary_WithTheCaughtUpSentence_InOneToast()
    {
        SignIn();
        EpisodeIs(1, 3);
        _progress
            .Setup(x => x.ToggleEpisodeWatchedAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EpisodeWatchResult(
                EpisodeWatchOutcome.MarkedWatched,
                new SeriesCaughtUp(SeriesId, "Silo", Finished: false),
                AddedToLibrary: "Silo"));

        await _sut.OnPostToggleWatchedAsync(EpisodeId, default);

        _sut.SuccessToast().Should().Be(
            "Marked S01E03 as watched and added Silo to your library. You're up to date with Silo. We'll let you know when a new episode airs.");
        _sut.InfoToast().Should().BeNull("one toast per action");
        _sut.TempData[PageModelToastExtensions.CaughtUpKindKey].Should().Be(PageModelToastExtensions.CaughtUpUpToDate);
    }

    [Test]
    public async Task Unmarking_Should_SayNothingAboutTheLibrary()
    {
        SignIn();
        EpisodeIs(1, 3);
        _progress
            .Setup(x => x.ToggleEpisodeWatchedAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EpisodeWatchResult(EpisodeWatchOutcome.MarkedUnwatched));

        await _sut.OnPostToggleWatchedAsync(EpisodeId, default);

        _sut.InfoToast().Should().Be("Episode marked as not watched.");
        _sut.SuccessToast().Should().BeNull();
    }

    [Test]
    public async Task MarkWatchedThrough_Should_SayTheSeriesWasAddedToTheLibrary_AndKeepTheUndo()
    {
        SignIn();
        EpisodeIs(1, 3);
        _progress
            .SetupSequence(x => x.MarkWatchedThroughAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarkWatchedThroughResult(EpisodeFound: true, MarkedCount: 3, Batch: new WatchedBatch(3, BatchStamp), AddedToLibrary: "Silo"))
            .ReturnsAsync(new MarkWatchedThroughResult(EpisodeFound: true, MarkedCount: 1, Batch: new WatchedBatch(1, BatchStamp), AddedToLibrary: "Silo"));

        await _sut.OnPostMarkWatchedThroughAsync(EpisodeId, default);
        _sut.SuccessToast().Should().Be("Marked 3 episodes as watched and added Silo to your library.");
        _sut.TempData[PageModelToastExtensions.UndoWatchedSeriesKey].Should().Be(SeriesId.ToString());

        await _sut.OnPostMarkWatchedThroughAsync(EpisodeId, default);
        _sut.SuccessToast().Should().Be("Marked S01E03 as watched and added Silo to your library.");
    }

    [Test]
    public async Task ToggleWatched_Should_SayFinished_WhenTheMarkFinishedTheSeries()
    {
        SignIn();
        EpisodeBelongsTo(SeriesId);
        _progress
            .Setup(x => x.ToggleEpisodeWatchedAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EpisodeWatchResult(
                EpisodeWatchOutcome.MarkedWatched, new SeriesCaughtUp(SeriesId, "Chernobyl", Finished: true, UserHasRated: true)));

        await _sut.OnPostToggleWatchedAsync(EpisodeId, default);

        _sut.SuccessToast().Should().Be("You've finished Chernobyl.");
        _sut.TempData[PageModelToastExtensions.CaughtUpKindKey].Should().Be(PageModelToastExtensions.CaughtUpFinished);
        _sut.TempData.ContainsKey(PageModelToastExtensions.RateSeriesKey).Should().BeFalse("the series is already rated");
    }

    [Test]
    public async Task MarkWatchedThrough_Should_AddUpToDateToTheCount_AndKeepTheUndo()
    {
        SignIn();
        EpisodeBelongsTo(SeriesId);
        _progress
            .Setup(x => x.MarkWatchedThroughAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarkWatchedThroughResult(
                EpisodeFound: true, MarkedCount: 3, Batch: new WatchedBatch(3, BatchStamp),
                CaughtUp: new SeriesCaughtUp(SeriesId, "Silo", Finished: false)));

        await _sut.OnPostMarkWatchedThroughAsync(EpisodeId, default);

        _sut.SuccessToast().Should().Be(
            "Marked 3 episodes as watched. You're up to date with Silo. We'll let you know when a new episode airs.");
        _sut.TempData[PageModelToastExtensions.UndoWatchedSeriesKey].Should().Be(SeriesId.ToString());
    }

    [Test]
    public async Task MarkWatchedThrough_Should_ReportTheCount_AndOfferAnUndoForTheParentSeries()
    {
        SignIn();
        EpisodeBelongsTo(SeriesId);
        _progress
            .Setup(x => x.MarkWatchedThroughAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarkWatchedThroughResult(EpisodeFound: true, MarkedCount: 3, Batch: new WatchedBatch(3, BatchStamp)));

        var result = await _sut.OnPostMarkWatchedThroughAsync(EpisodeId, default);

        AssertRedirectsBackToTheEpisode(result);
        _sut.SuccessToast().Should().Be("Marked 3 episodes as watched.");
        _sut.TempData[PageModelToastExtensions.UndoWatchedSeriesKey].Should().Be(SeriesId.ToString(), "the undo posts to the series, not the episode");
    }

    [Test]
    public async Task MarkWatchedThrough_Should_ExplainARefusal()
    {
        SignIn();
        EpisodeBelongsTo(SeriesId);
        _progress
            .SetupSequence(x => x.MarkWatchedThroughAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarkWatchedThroughResult(EpisodeFound: false, MarkedCount: 0))
            .ReturnsAsync(new MarkWatchedThroughResult(EpisodeFound: true, MarkedCount: 0, HasAired: false));

        await _sut.OnPostMarkWatchedThroughAsync(EpisodeId, default);
        _sut.ErrorToast().Should().Be("Could not verify this episode against its series right now.");

        await _sut.OnPostMarkWatchedThroughAsync(EpisodeId, default);
        _sut.ErrorToast().Should().Be("You can't mark an episode as watched before it has aired.");

        _sut.SuccessToast().Should().BeNull();
    }

    // ---- a mark resumes a stopped series ------------------------------------------------

    [Test]
    public async Task ToggleWatched_Should_SayTheSeriesWasResumed_WhenTheUserHadStoppedWatchingIt()
    {
        SignIn();
        EpisodeIs(3, 4);
        _progress
            .Setup(x => x.ToggleEpisodeWatchedAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EpisodeWatchResult(EpisodeWatchOutcome.MarkedWatched, ResumedWatching: "Silo"));

        await _sut.OnPostToggleWatchedAsync(EpisodeId, default);

        _sut.SuccessToast().Should().Be("Marked S03E04 as watched and resumed watching Silo.");
    }

    [Test]
    public async Task ToggleWatched_Should_CombineResumed_WithTheCaughtUpSentence_InOneToast()
    {
        SignIn();
        EpisodeIs(3, 4);
        _progress
            .Setup(x => x.ToggleEpisodeWatchedAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EpisodeWatchResult(
                EpisodeWatchOutcome.MarkedWatched,
                new SeriesCaughtUp(SeriesId, "Silo", Finished: false),
                ResumedWatching: "Silo"));

        await _sut.OnPostToggleWatchedAsync(EpisodeId, default);

        _sut.SuccessToast().Should().Be(
            "Marked S03E04 as watched and resumed watching Silo. You're up to date with Silo. We'll let you know when a new episode airs.");
        _sut.InfoToast().Should().BeNull("one toast per action");
    }

    [Test]
    public async Task MarkWatchedThrough_Should_SayTheSeriesWasResumed_AndKeepTheUndo()
    {
        SignIn();
        EpisodeIs(3, 4);
        _progress
            .SetupSequence(x => x.MarkWatchedThroughAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarkWatchedThroughResult(EpisodeFound: true, MarkedCount: 3, Batch: new WatchedBatch(3, BatchStamp), ResumedWatching: "Silo"))
            .ReturnsAsync(new MarkWatchedThroughResult(EpisodeFound: true, MarkedCount: 1, Batch: new WatchedBatch(1, BatchStamp), ResumedWatching: "Silo"));

        await _sut.OnPostMarkWatchedThroughAsync(EpisodeId, default);
        _sut.SuccessToast().Should().Be("Marked 3 episodes as watched and resumed watching Silo.");
        _sut.TempData[PageModelToastExtensions.UndoWatchedSeriesKey].Should().Be(SeriesId.ToString());

        await _sut.OnPostMarkWatchedThroughAsync(EpisodeId, default);
        _sut.SuccessToast().Should().Be("Marked S03E04 as watched and resumed watching Silo.");
    }

    [Test]
    public async Task MarkWatchedThrough_Should_PutTheStoppedDateInItsUndo_WhenTheMarkResumedTheSeries()
    {
        var stoppedOn = new DateTime(2026, 9, 4, 18, 0, 0, DateTimeKind.Utc);
        SignIn();
        EpisodeIs(3, 4);
        _progress
            .Setup(x => x.MarkWatchedThroughAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarkWatchedThroughResult(
                EpisodeFound: true, MarkedCount: 3, Batch: new WatchedBatch(3, BatchStamp), ResumedWatching: "Silo", ResumedFromStoppedUtc: stoppedOn));

        await _sut.OnPostMarkWatchedThroughAsync(EpisodeId, default);

        _sut.TempData[PageModelToastExtensions.UndoWatchedStoppedKey].Should().Be(stoppedOn.Ticks.ToString());
    }
}
