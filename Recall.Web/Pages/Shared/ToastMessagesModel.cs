using System.Globalization;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Display;
using Recall.Web.Services.Discover;
using Recall.Web.Services.PendingActions;

namespace Recall.Web.Pages.Shared;

/// <summary>
/// The toasts of one page load, read from TempData (written by
/// <see cref="PageModelToastExtensions"/>) for <c>_ToastMessages.cshtml</c>.
/// Everything the partial decides is decided here, so it can be tested
/// without rendering: which toasts there are, what each offers, and how long
/// each stays (<see cref="ToastDurations"/>).
/// </summary>
public sealed class ToastMessagesModel
{
    private ToastMessagesModel(IReadOnlyList<ToastView> toasts) => Toasts = toasts;

    /// <summary>In the order they are stacked; the last one is the newest (the one Escape closes).</summary>
    public IReadOnlyList<ToastView> Toasts { get; }

    /// <summary>
    /// The one toast the browser shows by itself, after Share copied the page's
    /// address (where the Web Share API is missing). It is drawn by <c>_Toast</c>
    /// into a <c>&lt;template data-toast-template="link-copied"&gt;</c> beside the
    /// Share button, and <c>RecallToasts.show</c> (js/tvdb-toasts.js) moves a
    /// copy into the stack: the same markup and timing as a toast from TempData.
    /// </summary>
    public static ToastView LinkCopied { get; } = new(ToastKind.Info, "Link copied.") { Icon = Icons.Info };

    public static ToastMessagesModel From(ITempDataDictionary tempData)
    {
        var toasts = new List<ToastView>();

        if (Text(tempData, PageModelToastExtensions.SuccessKey) is { } success)
        {
            // Set by the watched toasts when the mark brought the user up to date
            // with a series, or finished it: a different icon, and "Rate it" for
            // a finished series the user has not rated.
            var caughtUpKind = tempData.GetText(PageModelToastExtensions.CaughtUpKindKey);

            toasts.Add(new ToastView(ToastKind.Success, success)
            {
                CaughtUpKind = caughtUpKind,
                Icon = caughtUpKind switch
                {
                    PageModelToastExtensions.CaughtUpFinished => Icons.Finished + " tvdb-toast-icon--finished",
                    PageModelToastExtensions.CaughtUpUpToDate => Icons.UpToDate,
                    _ => Icons.Success
                },
                RateSeriesId = Id(tempData, PageModelToastExtensions.RateSeriesKey),
                UndoWatched = UndoWatchedFrom(tempData),
                UndoStoppedSeriesId = Id(tempData, PageModelToastExtensions.UndoStoppedSeriesKey),
                UndoDiscover = UndoDiscoverFrom(tempData),
                UndoAdd = UndoAddFrom(tempData)
            });
        }

        if (Text(tempData, PageModelToastExtensions.ErrorKey) is { } error)
            toasts.Add(new ToastView(ToastKind.Error, error) { Icon = Icons.Error });

        if (Text(tempData, PageModelToastExtensions.WarningKey) is { } warning)
            toasts.Add(new ToastView(ToastKind.Warning, warning) { Icon = Icons.Warning });

        if (Text(tempData, PageModelToastExtensions.InfoKey) is { } info)
            toasts.Add(new ToastView(ToastKind.Info, info) { Icon = Icons.Info });

        return new ToastMessagesModel(toasts);
    }

    // Every read goes through TempDataValueExtensions: the cookie serializer
    // gives values back as other types than were written (a GUID-shaped string
    // as a Guid), so "as string" would silently lose them.
    private static string? Text(ITempDataDictionary tempData, string key) =>
        tempData.GetText(key) is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;

    /// <summary>A TheTVDB id (written as invariant text), back as invariant text; null when absent or not a positive number.</summary>
    private static string? Id(ITempDataDictionary tempData, string key) =>
        tempData.GetInt32(key) is > 0 and var id ? id.ToString(CultureInfo.InvariantCulture) : null;

    private static UndoDiscoverForm? UndoDiscoverFrom(ITempDataDictionary tempData)
    {
        var action = tempData.GetText(PageModelToastExtensions.UndoDiscoverActionKey);
        var kind = tempData.GetText(PageModelToastExtensions.UndoDiscoverKindKey);
        var id = Id(tempData, PageModelToastExtensions.UndoDiscoverIdKey);

        if (!IsName<DiscoverUndoAction>(action, out var parsedAction) || !IsName<DiscoverKind>(kind, out var parsedKind) || id is null)
            return null;

        return new UndoDiscoverForm(parsedAction.ToString(), parsedKind.ToString(), id, Text(tempData, PageModelToastExtensions.UndoDiscoverAnchorKey));
    }

    /// <summary>An enum value written by its name (as the helpers write it), never a number.</summary>
    private static bool IsName<T>(string? value, out T parsed) where T : struct, Enum =>
        Enum.TryParse(value, ignoreCase: false, out parsed) && Enum.IsDefined(parsed) && !int.TryParse(value, out _);

    private static UndoAddForm? UndoAddFrom(ITempDataDictionary tempData)
    {
        var title = tempData.GetText(PageModelToastExtensions.UndoAddTitleKey);
        var id = Id(tempData, PageModelToastExtensions.UndoAddIdKey);

        return IsName<PendingTitle>(title, out var parsed) && parsed is PendingTitle.Series or PendingTitle.Movie && id is not null
            ? new UndoAddForm(parsed == PendingTitle.Series, id)
            : null;
    }

    private static UndoWatchedForm? UndoWatchedFrom(ITempDataDictionary tempData)
    {
        var seriesId = Id(tempData, PageModelToastExtensions.UndoWatchedSeriesKey);
        var batchId = tempData.GetGuid(PageModelToastExtensions.UndoWatchedBatchKey);
        var stoppedTicks = tempData.GetInt64(PageModelToastExtensions.UndoWatchedStoppedKey);

        if (seriesId is null || batchId is not { } batch || batch == Guid.Empty)
            return null;

        return new UndoWatchedForm(
            seriesId,
            batch.ToString("D"),
            stoppedTicks?.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>What the Undo of a Discover add or "Not interested" posts back: "Add" or "Hide", "Series" or "Movie", the id, and the card's place.</summary>
public sealed record UndoDiscoverForm(string Action, string Kind, string Id, string? Anchor);

/// <summary>The Undo of an add done after sign-in: the series out of the library again, or the movie off the watchlist again.</summary>
public sealed record UndoAddForm(bool IsSeries, string Id);

/// <summary>What the Undo of a "mark watched" posts back: the batch, and the stopped date to restore when the mark had resumed the series.</summary>
public sealed record UndoWatchedForm(string SeriesId, string Batch, string? StoppedStamp);

/// <summary>One toast.</summary>
public sealed record ToastView(ToastKind Kind, string Text)
{
    /// <summary>Icon class, from <c>Icons</c> (with a modifier for the "finished" trophy).</summary>
    public required string Icon { get; init; }

    /// <summary>"Finished" or "UpToDate" on a caught-up toast (its layout on a phone), else null.</summary>
    public string? CaughtUpKind { get; init; }

    /// <summary>The series a "Rate it" link is offered for.</summary>
    public string? RateSeriesId { get; init; }

    /// <summary>The Undo of a bulk (or one-tap) mark.</summary>
    public UndoWatchedForm? UndoWatched { get; init; }

    /// <summary>The series the Undo of a "Stop watching" resumes.</summary>
    public string? UndoStoppedSeriesId { get; init; }

    /// <summary>The Undo of an add or a "Not interested" on Discover.</summary>
    public UndoDiscoverForm? UndoDiscover { get; init; }

    /// <summary>The Undo of an add done after sign-in (a pending action).</summary>
    public UndoAddForm? UndoAdd { get; init; }

    /// <summary>Whether the toast has something to press besides its close button. A new action must be counted here.</summary>
    public bool HasAction =>
        RateSeriesId is not null || UndoWatched is not null || UndoStoppedSeriesId is not null || UndoDiscover is not null
        || UndoAdd is not null;

    /// <summary>
    /// Milliseconds until the toast closes itself, for <c>data-toast-duration</c>;
    /// null (no attribute, no countdown bar) for one that stays until closed.
    /// </summary>
    public int? DurationMilliseconds =>
        ToastDurations.For(Kind, HasAction) is { } duration ? (int)duration.TotalMilliseconds : null;

    /// <summary>Bootstrap's alert class for the kind.</summary>
    public string CssClass => Kind switch
    {
        ToastKind.Success => "alert-success",
        ToastKind.Info => "alert-info",
        ToastKind.Warning => "alert-warning",
        _ => "alert-danger"
    };
}
