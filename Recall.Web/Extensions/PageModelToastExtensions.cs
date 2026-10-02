using System.Globalization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Web.Extensions;

public static class PageModelToastExtensions
{
    private const string SuccessKey = "Toast.Success";
    private const string ErrorKey = "Toast.Error";
    private const string InfoKey = "Toast.Info";

    // Read back by _ToastMessages.cshtml, which renders an "Undo" POST form
    // inside the success toast when both are present.
    public const string UndoWatchedSeriesKey = "Toast.UndoWatched.SeriesId";
    public const string UndoWatchedStampKey = "Toast.UndoWatched.Stamp";

    extension(PageModel pageModel)
    {
        public void SetSuccessToast(string message)
            => pageModel.TempData[SuccessKey] = message;

        public void SetErrorToast(string message)
            => pageModel.TempData[ErrorKey] = message;

        public void SetInfoToast(string message)
            => pageModel.TempData[InfoKey] = message;

        /// <summary>
        /// Success toast for a "mark watched" that also offers to undo it.
        /// By default the undo is only offered when the batch wrote more than
        /// one row — on a series page a single episode is undone by clicking
        /// its tick again. Pass <paramref name="undoSingle"/> where the episode
        /// leaves the page once marked (a Dashboard catch-up card), so there is
        /// no tick left to click.
        /// </summary>
        public void SetSuccessToastWithWatchedUndo(string message, int seriesTvdbId, WatchedBatch? batch, bool undoSingle = false)
        {
            pageModel.TempData[SuccessKey] = message;

            if (batch is null || batch.InsertedCount < (undoSingle ? 1 : 2))
                return;

            // Strings: the cookie TempData serializer only round-trips a few primitive types.
            pageModel.TempData[UndoWatchedSeriesKey] = seriesTvdbId.ToString(CultureInfo.InvariantCulture);
            pageModel.TempData[UndoWatchedStampKey] = batch.WatchedUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }
    }
}