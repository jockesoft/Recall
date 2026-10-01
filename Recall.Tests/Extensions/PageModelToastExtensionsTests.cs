using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Moq;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Repositories;

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
}
