using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Tests.TestSupport;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Display;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages.Shared;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Pages;

/// <summary>
/// How long each toast stays (<see cref="ToastDurations"/>), decided from what
/// the page models' toast helpers wrote: 5 seconds for a plain success or info
/// toast, 10 for one with an action, never for an error or a warning.
/// </summary>
[TestFixture]
public sealed class ToastMessagesModelTests
{
    private static readonly DateTime Stamp = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 10, 6);

    private sealed class TestPage : PageModel;

    private static TestPage Page() => new TestPage().WithTempData();

    private static IReadOnlyList<ToastView> ToastsOf(PageModel page) => ToastMessagesModel.From(page.TempData).Toasts;

    private static ToastView OnlyToastOf(PageModel page) => ToastsOf(page).Should().ContainSingle().Subject;

    // ---- the rule itself -----------------------------------------------------------

    [Test]
    public void Durations_Should_BeFiveSecondsPlain_TenWithAnAction_AndNoneForErrorsAndWarnings()
    {
        ToastDurations.Plain.Should().Be(TimeSpan.FromSeconds(5));
        ToastDurations.WithAction.Should().Be(TimeSpan.FromSeconds(10));

        ToastDurations.For(ToastKind.Success, hasAction: false).Should().Be(ToastDurations.Plain);
        ToastDurations.For(ToastKind.Info, hasAction: false).Should().Be(ToastDurations.Plain);
        ToastDurations.For(ToastKind.Success, hasAction: true).Should().Be(ToastDurations.WithAction);
        ToastDurations.For(ToastKind.Info, hasAction: true).Should().Be(ToastDurations.WithAction);

        foreach (var hasAction in new[] { false, true })
        {
            ToastDurations.For(ToastKind.Error, hasAction).Should().BeNull("an error never closes on its own");
            ToastDurations.For(ToastKind.Warning, hasAction).Should().BeNull("a warning never closes on its own");
        }
    }

    [Test]
    public void EveryKind_Should_HaveARule()
    {
        foreach (var kind in Enum.GetValues<ToastKind>())
        {
            var closes = ToastDurations.For(kind, hasAction: false) is not null;
            closes.Should().Be(kind is ToastKind.Success or ToastKind.Info, $"{kind} must be decided on purpose");
        }
    }

    // ---- what each helper's toast gets ----------------------------------------------

    [Test]
    public void APlainSuccessToast_Should_CloseAfterFiveSeconds()
    {
        var page = Page();
        page.SetSuccessToast("Rating saved.");

        var toast = OnlyToastOf(page);

        toast.Kind.Should().Be(ToastKind.Success);
        toast.Text.Should().Be("Rating saved.");
        toast.HasAction.Should().BeFalse();
        toast.DurationMilliseconds.Should().Be(5000);
        toast.CssClass.Should().Be("alert-success");
    }

    [Test]
    public void AnInfoToast_Should_CloseAfterFiveSeconds()
    {
        var page = Page();
        page.SetInfoToast("Episode marked as not watched.");

        var toast = OnlyToastOf(page);

        toast.Kind.Should().Be(ToastKind.Info);
        toast.DurationMilliseconds.Should().Be(5000);
        toast.CssClass.Should().Be("alert-info");
    }

    [Test]
    public void AnErrorToast_Should_NeverCloseItself()
    {
        var page = Page();
        page.SetErrorToast("Could not update watched status right now.");

        var toast = OnlyToastOf(page);

        toast.Kind.Should().Be(ToastKind.Error);
        toast.DurationMilliseconds.Should().BeNull();
        toast.CssClass.Should().Be("alert-danger");
    }

    [Test]
    public void AWarningToast_Should_NeverCloseItself()
    {
        var page = Page();
        page.SetWarningToast("Some rows could not be read.");

        var toast = OnlyToastOf(page);

        toast.Kind.Should().Be(ToastKind.Warning);
        toast.DurationMilliseconds.Should().BeNull();
        toast.CssClass.Should().Be("alert-warning");
        toast.Icon.Should().Be(Icons.Warning);
    }

    [Test]
    public void AToastWithAnUndoOfAMark_Should_CloseAfterTenSeconds()
    {
        var page = Page();
        page.SetSuccessToastWithWatchedUndo("Marked 3 episodes as watched.", 42, new WatchedBatch(3, Stamp));

        var toast = OnlyToastOf(page);

        toast.UndoWatched.Should().Be(new UndoWatchedForm("42", Stamp.Ticks.ToString(), StoppedStamp: null));
        toast.HasAction.Should().BeTrue();
        toast.DurationMilliseconds.Should().Be(10000, "it no longer stays until dismissed");
    }

    [Test]
    public void TheSameToast_WithoutItsUndo_Should_CloseAfterFiveSeconds()
    {
        var page = Page();
        page.SetSuccessToastWithWatchedUndo("Episode marked as watched.", 42, new WatchedBatch(1, Stamp));

        var toast = OnlyToastOf(page);

        toast.UndoWatched.Should().BeNull("one row on a series page is undone by its tick");
        toast.DurationMilliseconds.Should().Be(5000);
    }

    [Test]
    public void AnUndoThatRestoresAStop_Should_CarryTheStoppedStamp()
    {
        var stoppedUtc = new DateTime(2026, 9, 4, 18, 0, 0, DateTimeKind.Utc);
        var page = Page();
        page.SetSuccessToastWithWatchedUndo("Marked 3 episodes as watched and resumed watching Silo.", 42, new WatchedBatch(3, Stamp),
            resumedFromStoppedUtc: stoppedUtc);

        OnlyToastOf(page).UndoWatched!.StoppedStamp.Should().Be(stoppedUtc.Ticks.ToString());
    }

    [Test]
    public void ARateItToast_Should_CloseAfterTenSeconds_AndAPlainCaughtUpToastAfterFive()
    {
        var finished = Page();
        finished.SetWatchedToast("Episode marked as watched.", new SeriesCaughtUp(42, "Chernobyl", Finished: true, UserHasRated: false), Today);

        var rateIt = OnlyToastOf(finished);
        rateIt.RateSeriesId.Should().Be("42");
        rateIt.CaughtUpKind.Should().Be(PageModelToastExtensions.CaughtUpFinished);
        rateIt.Icon.Should().StartWith(Icons.Finished);
        rateIt.DurationMilliseconds.Should().Be(10000);

        var upToDate = Page();
        upToDate.SetWatchedToast("Episode marked as watched.", new SeriesCaughtUp(42, "Silo", Finished: false), Today);

        var plain = OnlyToastOf(upToDate);
        plain.Icon.Should().Be(Icons.UpToDate);
        plain.HasAction.Should().BeFalse();
        plain.DurationMilliseconds.Should().Be(5000);
    }

    [Test]
    public void TheStoppedWatchingToast_Should_CloseAfterTenSeconds_AndItsRefusalsFollowTheirKind()
    {
        var stopped = Page();
        stopped.SetStopWatchingToast(new StopWatchingResult(StopWatchingOutcome.Stopped, "Silo"), 42);
        var withUndo = OnlyToastOf(stopped);
        withUndo.UndoStoppedSeriesId.Should().Be("42");
        withUndo.DurationMilliseconds.Should().Be(10000);

        var already = Page();
        already.SetStopWatchingToast(new StopWatchingResult(StopWatchingOutcome.AlreadyStopped, "Silo"), 42);
        OnlyToastOf(already).DurationMilliseconds.Should().Be(5000, "an info toast");

        var missing = Page();
        missing.SetStopWatchingToast(new StopWatchingResult(StopWatchingOutcome.NotInLibrary), 42);
        OnlyToastOf(missing).DurationMilliseconds.Should().BeNull("an error toast");
    }

    [Test]
    public void SeveralToasts_Should_EachGetTheirOwnDuration_WithTheNewestLast()
    {
        var page = Page();
        page.SetSuccessToastWithWatchedUndo("Marked 3 episodes as watched.", 42, new WatchedBatch(3, Stamp));
        page.SetErrorToast("Could not load your rating.");
        page.SetInfoToast("Rating removed.");

        var toasts = ToastsOf(page);

        toasts.Select(t => t.Kind).Should().Equal(ToastKind.Success, ToastKind.Error, ToastKind.Info);
        toasts.Select(t => t.DurationMilliseconds).Should().Equal(10000, null, 5000);
    }

    [Test]
    public void NoToast_Should_BeShown_ForAnEmptyOrBlankMessage()
    {
        var page = Page();
        page.SetSuccessToast(" ");

        ToastsOf(page).Should().BeEmpty();
        ToastsOf(Page()).Should().BeEmpty();
    }

    [Test]
    public void ActionKeys_Should_BeIgnored_WhenTheyAreNotWhatTheHelpersWrite()
    {
        var page = Page();
        page.SetSuccessToast("Saved.");
        page.TempData[PageModelToastExtensions.UndoWatchedSeriesKey] = "not-a-number";
        page.TempData[PageModelToastExtensions.UndoWatchedStampKey] = "nor-this";
        page.TempData[PageModelToastExtensions.RateSeriesKey] = "x";

        var toast = OnlyToastOf(page);

        toast.HasAction.Should().BeFalse();
        toast.DurationMilliseconds.Should().Be(5000);
    }
}
