using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Moq;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Extensions;

[TestFixture]
public class PageModelToastExtensionsTests
{
    private static readonly DateTime Stamp = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class TestPageModel : PageModel;

    private static TestPageModel CreatePage() => new()
    {
        TempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>())
    };

    [Test]
    public void SetSuccessToastWithWatchedUndo_Should_OfferAnUndo_ForABatchOfSeveralEpisodes()
    {
        var page = CreatePage();

        page.SetSuccessToastWithWatchedUndo("Marked 3 episodes as watched.", seriesTvdbId: 42, new WatchedBatch(3, Stamp));

        page.TempData["Toast.Success"].Should().Be("Marked 3 episodes as watched.");
        page.TempData[PageModelToastExtensions.UndoWatchedSeriesKey].Should().Be("42");
        page.TempData[PageModelToastExtensions.UndoWatchedStampKey].Should().Be(Stamp.Ticks.ToString());
    }

    [Test]
    public void SetSuccessToastWithWatchedUndo_Should_NotOfferAnUndo_WhenAtMostOneRowWasWritten()
    {
        foreach (var batch in new[] { null, WatchedBatch.Empty, new WatchedBatch(1, Stamp) })
        {
            var page = CreatePage();

            page.SetSuccessToastWithWatchedUndo("Episode marked as watched.", seriesTvdbId: 42, batch);

            page.TempData["Toast.Success"].Should().Be("Episode marked as watched.");
            page.TempData.ContainsKey(PageModelToastExtensions.UndoWatchedSeriesKey).Should().BeFalse();
            page.TempData.ContainsKey(PageModelToastExtensions.UndoWatchedStampKey).Should().BeFalse();
        }
    }

    // ---- "You're up to date" / "You've finished" ---------------------------------

    private static readonly DateOnly Today = new(2026, 10, 2);
    private static readonly SeriesCaughtUp FinishedUnrated = new(42, "Chernobyl", Finished: true, UserHasRated: false);
    private static readonly SeriesCaughtUp FinishedRated = new(42, "Chernobyl", Finished: true, UserHasRated: true);
    private static readonly SeriesCaughtUp UpToDate = new(42, "Silo", Finished: false);

    [Test]
    public void SetWatchedToast_Should_SayTheOrdinaryMessage_WhenTheMarkDidNotCatchUp()
    {
        var page = CreatePage();

        page.SetWatchedToast("Episode marked as watched.", caughtUp: null, Today);

        page.TempData["Toast.Success"].Should().Be("Episode marked as watched.");
        page.TempData.ContainsKey(PageModelToastExtensions.CaughtUpKindKey).Should().BeFalse();
        page.TempData.ContainsKey(PageModelToastExtensions.RateSeriesKey).Should().BeFalse();
    }

    [Test]
    public void SetWatchedToast_Should_BecomeTheCaughtUpToast_NotASecondOne()
    {
        var page = CreatePage();

        page.SetWatchedToast("Episode marked as watched.", UpToDate, Today);

        page.TempData["Toast.Success"].Should().Be("You're up to date with Silo. We'll let you know when a new episode airs.");
        page.TempData[PageModelToastExtensions.CaughtUpKindKey].Should().Be(PageModelToastExtensions.CaughtUpUpToDate);
        page.TempData.ContainsKey(PageModelToastExtensions.RateSeriesKey).Should().BeFalse();
        page.TempData.ContainsKey("Toast.Info").Should().BeFalse("one toast per action");
    }

    [Test]
    public void AFinishedSeries_Should_OfferRateIt_OnlyWhenTheUserHasNotRatedIt()
    {
        var unrated = CreatePage();
        unrated.SetWatchedToast("Episode marked as watched.", FinishedUnrated, Today);

        unrated.TempData["Toast.Success"].Should().Be("You've finished Chernobyl.");
        unrated.TempData[PageModelToastExtensions.CaughtUpKindKey].Should().Be(PageModelToastExtensions.CaughtUpFinished);
        unrated.TempData[PageModelToastExtensions.RateSeriesKey].Should().Be("42");

        var rated = CreatePage();
        rated.SetWatchedToast("Episode marked as watched.", FinishedRated, Today);

        rated.TempData["Toast.Success"].Should().Be("You've finished Chernobyl.");
        rated.TempData.ContainsKey(PageModelToastExtensions.RateSeriesKey).Should().BeFalse();
    }

    [Test]
    public void ABulkMarkThatCatchesUp_Should_BeOneCombinedToast_ThatKeepsItsUndo()
    {
        var page = CreatePage();

        page.SetSuccessToastWithWatchedUndo(
            "Marked 8 episodes as watched.", seriesTvdbId: 42, new WatchedBatch(8, Stamp), caughtUp: FinishedUnrated, today: Today);

        page.TempData["Toast.Success"].Should().Be("Marked 8 episodes as watched. You've finished Chernobyl.");
        page.TempData[PageModelToastExtensions.UndoWatchedSeriesKey].Should().Be("42");
        page.TempData[PageModelToastExtensions.UndoWatchedStampKey].Should().Be(Stamp.Ticks.ToString());
        page.TempData[PageModelToastExtensions.CaughtUpKindKey].Should().Be(PageModelToastExtensions.CaughtUpFinished);
        page.TempData[PageModelToastExtensions.RateSeriesKey].Should().Be("42");
    }

    [Test]
    public void ABulkMarkThatDoesNotCatchUp_Should_BeTheToastItAlwaysWas()
    {
        var page = CreatePage();

        page.SetSuccessToastWithWatchedUndo("Marked 3 episodes as watched.", seriesTvdbId: 42, new WatchedBatch(3, Stamp));

        page.TempData["Toast.Success"].Should().Be("Marked 3 episodes as watched.");
        page.TempData.ContainsKey(PageModelToastExtensions.CaughtUpKindKey).Should().BeFalse();
    }
}
