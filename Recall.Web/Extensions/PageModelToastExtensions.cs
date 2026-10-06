using System.Globalization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Extensions;

public static class PageModelToastExtensions
{
    // Read back by ToastMessagesModel, which also decides how long each toast stays.
    public const string SuccessKey = "Toast.Success";
    public const string ErrorKey = "Toast.Error";
    public const string InfoKey = "Toast.Info";
    public const string WarningKey = "Toast.Warning";

    // Read back by _ToastMessages.cshtml, which renders an "Undo" POST form
    // inside the success toast when both are present.
    public const string UndoWatchedSeriesKey = "Toast.UndoWatched.SeriesId";
    public const string UndoWatchedStampKey = "Toast.UndoWatched.Stamp";

    // With the two above, when the mark resumed a series the user had stopped
    // watching: the stopped date (ticks) the Undo puts back.
    public const string UndoWatchedStoppedKey = "Toast.UndoWatched.StoppedStamp";

    // Read back by _ToastMessages.cshtml, which renders an "Undo" POST form
    // (resume the series) inside the "Stopped watching …" toast.
    public const string UndoStoppedSeriesKey = "Toast.UndoStopped.SeriesId";

    // Set when the success toast is the "you're up to date" / "you've finished"
    // one: which of the two (its icon), and the series to offer "Rate it" for.
    public const string CaughtUpKindKey = "Toast.CaughtUp.Kind";
    public const string RateSeriesKey = "Toast.CaughtUp.RateSeriesId";
    public const string CaughtUpFinished = "Finished";
    public const string CaughtUpUpToDate = "UpToDate";

    extension(PageModel pageModel)
    {
        public void SetSuccessToast(string message)
            => pageModel.TempData[SuccessKey] = message;

        public void SetErrorToast(string message)
            => pageModel.TempData[ErrorKey] = message;

        public void SetInfoToast(string message)
            => pageModel.TempData[InfoKey] = message;

        /// <summary>
        /// Something the user should know went less than right, short of an
        /// error. Like an error it stays until it is closed.
        /// </summary>
        public void SetWarningToast(string message)
            => pageModel.TempData[WarningKey] = message;

        /// <summary>
        /// The toast for a "Stop watching", from whichever page it was pressed
        /// on: "Stopped watching Silo." with an Undo (a POST that resumes the
        /// series), or why nothing was stopped.
        /// </summary>
        public void SetStopWatchingToast(StopWatchingResult result, int seriesTvdbId)
        {
            switch (result.Outcome)
            {
                case StopWatchingOutcome.Stopped:
                    pageModel.TempData[SuccessKey] = $"Stopped watching {result.SeriesName}.";
                    pageModel.TempData[UndoStoppedSeriesKey] = seriesTvdbId.ToString(CultureInfo.InvariantCulture);
                    break;
                case StopWatchingOutcome.AlreadyStopped:
                    pageModel.TempData[InfoKey] = $"You had already stopped watching {result.SeriesName}.";
                    break;
                case StopWatchingOutcome.Finished:
                    pageModel.TempData[InfoKey] = $"You've finished {result.SeriesName}, so there is nothing to stop.";
                    break;
                default:
                    pageModel.TempData[ErrorKey] = "That series isn't in your library.";
                    break;
            }
        }

        /// <summary>
        /// Success toast for marking one episode watched. When the mark brought
        /// the user up to date with the series, or finished it
        /// (<paramref name="caughtUp"/>, from <c>IWatchProgressService</c>), the
        /// toast says that instead: one toast per action, never two. Pass
        /// <paramref name="keepMessage"/> when the message itself says something
        /// that must not be lost (the series was added to the library): the
        /// caught-up sentence then follows it.
        /// </summary>
        public void SetWatchedToast(string message, SeriesCaughtUp? caughtUp, DateOnly today, bool keepMessage = false)
        {
            pageModel.TempData[SuccessKey] = caughtUp is null
                ? message
                : keepMessage ? $"{message} {caughtUp.Sentence(today)}" : caughtUp.Sentence(today);
            pageModel.MarkCaughtUp(caughtUp);
        }

        /// <summary>Which caught-up toast this is, for its icon and its "Rate it" link. Nothing when the mark did not catch up.</summary>
        private void MarkCaughtUp(SeriesCaughtUp? caughtUp)
        {
            if (caughtUp is null)
                return;

            pageModel.TempData[CaughtUpKindKey] = caughtUp.Finished ? CaughtUpFinished : CaughtUpUpToDate;

            if (caughtUp.OfferRating)
                pageModel.TempData[RateSeriesKey] = caughtUp.SeriesTvdbId.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Success toast for a "mark watched" that also offers to undo it.
        /// By default the undo is only offered when the batch wrote more than
        /// one row — on a series page a single episode is undone by clicking
        /// its tick again. Pass <paramref name="undoSingle"/> where the episode
        /// leaves the page once marked (a Dashboard catch-up card), so there is
        /// no tick left to click. When the mark caught the user up
        /// (<paramref name="caughtUp"/>), that sentence is added to the same
        /// toast, after what was marked: "Marked 8 episodes as watched. You've
        /// finished Chernobyl." The Undo stays. When the mark resumed a series
        /// the user had stopped watching, pass the result's
        /// <c>ResumedFromStoppedUtc</c> as <paramref name="resumedFromStoppedUtc"/>:
        /// the Undo then also stops the series again, with that date.
        /// </summary>
        public void SetSuccessToastWithWatchedUndo(
            string message, int seriesTvdbId, WatchedBatch? batch, bool undoSingle = false,
            SeriesCaughtUp? caughtUp = null, DateOnly today = default, DateTime? resumedFromStoppedUtc = null)
        {
            pageModel.TempData[SuccessKey] = caughtUp is null ? message : $"{message} {caughtUp.Sentence(today)}";
            pageModel.MarkCaughtUp(caughtUp);

            if (batch is null || batch.InsertedCount < (undoSingle ? 1 : 2))
                return;

            // Strings: the cookie TempData serializer only round-trips a few primitive types.
            pageModel.TempData[UndoWatchedSeriesKey] = seriesTvdbId.ToString(CultureInfo.InvariantCulture);
            pageModel.TempData[UndoWatchedStampKey] = batch.WatchedUtc.Ticks.ToString(CultureInfo.InvariantCulture);

            if (resumedFromStoppedUtc is { } stoppedUtc)
                pageModel.TempData[UndoWatchedStoppedKey] = stoppedUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }
    }
}