using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Tests.TestSupport;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Pages.Shared;
using Recall.Web.Services.Discover;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Pages;

/// <summary>
/// Every toast the helpers in <see cref="PageModelToastExtensions"/> can write,
/// carried through the real TempData cookie (<see cref="CookieTempData"/>) and
/// read by <see cref="ToastMessagesModel"/>, must read exactly as it does in
/// the request that wrote it. Until 2026-11-22 the Undo of every bulk mark was
/// lost on the way: the cookie serializer returns the batch id, a GUID-shaped
/// string, as a <see cref="Guid"/>, and the model read it with "as string".
/// </summary>
[TestFixture]
public sealed class ToastTempDataRoundTripTests
{
    private static readonly Guid BatchId = new("0b5e0c1d-7a6f-4a1e-9a55-2f4c7c1a9d01");
    private static readonly DateOnly Today = new(2026, 11, 22);
    private static readonly DateTime StoppedUtc = new(2026, 9, 4, 18, 30, 15, 123, DateTimeKind.Utc);

    /// <summary>One way of setting toasts, as a page handler does.</summary>
    public sealed record Scenario(string Name, Action<PageModel> Write)
    {
        public override string ToString() => Name;
    }

    public static IEnumerable<Scenario> Scenarios()
    {
        yield return new("success", p => p.SetSuccessToast("Added Silo to your library."));
        yield return new("error", p => p.SetErrorToast("Could not update watched status right now."));
        yield return new("info", p => p.SetInfoToast("Nothing to mark — every aired episode in this season is already watched."));
        yield return new("warning", p => p.SetWarningToast("Something to know."));

        foreach (var outcome in Enum.GetValues<StopWatchingOutcome>())
            yield return new($"stop watching: {outcome}", p => p.SetStopWatchingToast(new StopWatchingResult(outcome, "Silo"), 403245));

        yield return new("watched, finished and unrated (Rate it)",
            p => p.SetWatchedToast("Marked S01E05 as watched.", new SeriesCaughtUp(87108, "Chernobyl", Finished: true, UserHasRated: false), Today));
        yield return new("watched, up to date",
            p => p.SetWatchedToast("Marked S02E10 as watched.", new SeriesCaughtUp(403245, "Silo", Finished: false), Today, keepMessage: true));

        yield return new("bulk mark with Undo",
            p => p.SetSuccessToastWithWatchedUndo("Marked 7 episodes as watched.", 370112, new WatchedBatch(7, BatchId)));
        yield return new("one-tap mark with Undo",
            p => p.SetSuccessToastWithWatchedUndo("Marked S02E05 as watched.", 403245, new WatchedBatch(1, BatchId), undoSingle: true));
        yield return new("bulk mark that resumed a stopped series, finished, with Undo and Rate it",
            p => p.SetSuccessToastWithWatchedUndo(
                "Marked 7 episodes as watched and resumed watching Mare of Easttown.", 370112, new WatchedBatch(7, BatchId),
                caughtUp: new SeriesCaughtUp(370112, "Mare of Easttown", Finished: true, UserHasRated: false),
                today: Today, resumedFromStoppedUtc: StoppedUtc));
        yield return new("Discover add of a series, with Undo",
            p => p.SetDiscoverToast("Added Severance to your library.", new DiscoverUndo(DiscoverUndoAction.Add, DiscoverKind.Series, 371980, "discover-new-5")));
        yield return new("Discover add of a movie, with Undo",
            p => p.SetDiscoverToast("Added Dune: Part Two to your watchlist.", new DiscoverUndo(DiscoverUndoAction.Add, DiscoverKind.Movie, 338552, "discover-movies-20")));
        yield return new("Discover hide, with Undo",
            p => p.SetDiscoverToast("Hidden from Discover.", new DiscoverUndo(DiscoverUndoAction.Hide, DiscoverKind.Series, 445000, "discover-genre-science-fiction-1")));
        yield return new("Discover hide without a place",
            p => p.SetDiscoverToast("Hidden from Discover.", new DiscoverUndo(DiscoverUndoAction.Hide, DiscoverKind.Movie, 1, null)));
        yield return new("success and error together",
            p => { p.SetSuccessToast("Saved."); p.SetErrorToast("But something else failed."); });
    }

    [TestCaseSource(nameof(Scenarios))]
    public void EveryToast_Should_ReadTheSame_AfterTheCookie(Scenario scenario)
    {
        var inRequest = ToastMessagesModel.From(CookieTempData.InRequest(scenario.Write)).Toasts;
        var afterCookie = ToastMessagesModel.From(CookieTempData.RoundTrip(scenario.Write)).Toasts;

        inRequest.Should().NotBeEmpty();
        afterCookie.Should().BeEquivalentTo(inRequest, options => options.WithStrictOrdering(),
            "what the cookie brings back must read as what the handler wrote");
    }

    [Test]
    public void TheUndoOfABulkMark_Should_SurviveTheCookie_ThoughItsBatchIdComesBackAsAGuid()
    {
        void Write(PageModel p) =>
            p.SetSuccessToastWithWatchedUndo("Marked 7 episodes as watched.", 370112, new WatchedBatch(7, BatchId), resumedFromStoppedUtc: StoppedUtc);

        var tempData = CookieTempData.RoundTrip(Write);

        // Why the model must not read it "as string": the serializer's own conversion.
        tempData[PageModelToastExtensions.UndoWatchedBatchKey].Should().BeOfType<Guid>();

        var toast = ToastMessagesModel.From(tempData).Toasts.Should().ContainSingle().Subject;
        toast.UndoWatched.Should().Be(new UndoWatchedForm("370112", BatchId.ToString("D"), StoppedUtc.Ticks.ToString()));
        toast.HasAction.Should().BeTrue();
        toast.DurationMilliseconds.Should().Be(10_000, "a toast with an Undo stays ten seconds");
    }

    [Test]
    public void TheUndoOfADiscoverHide_Should_SurviveTheCookie()
    {
        var tempData = CookieTempData.RoundTrip(p =>
            p.SetDiscoverToast("Hidden from Discover.", new DiscoverUndo(DiscoverUndoAction.Hide, DiscoverKind.Movie, 338552, "discover-movies-3")));

        var toast = ToastMessagesModel.From(tempData).Toasts.Should().ContainSingle().Subject;
        toast.Text.Should().Be("Hidden from Discover.");
        toast.UndoDiscover.Should().Be(new UndoDiscoverForm("Hide", "Movie", "338552", "discover-movies-3"));
        toast.HasAction.Should().BeTrue();
        toast.DurationMilliseconds.Should().Be(10_000, "a toast with an Undo stays ten seconds");
    }

    [Test]
    public void EveryKeyTheHelpersWrite_Should_BeWrittenByAScenario_AndComeBackFromTheCookie()
    {
        var keys = typeof(PageModelToastExtensions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name.EndsWith("Key", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        keys.Should().NotBeEmpty();

        var returned = Scenarios()
            .SelectMany(s => CookieTempData.RoundTrip(s.Write).Keys)
            .ToHashSet();

        returned.Should().Contain(keys,
            "a new TempData key needs a scenario here, so its value is shown to survive the cookie");
    }
}
