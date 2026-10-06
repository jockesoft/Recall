using System.Globalization;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Display;

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

    public static ToastMessagesModel From(ITempDataDictionary tempData)
    {
        var toasts = new List<ToastView>();

        if (Text(tempData, PageModelToastExtensions.SuccessKey) is { } success)
        {
            // Set by the watched toasts when the mark brought the user up to date
            // with a series, or finished it: a different icon, and "Rate it" for
            // a finished series the user has not rated.
            var caughtUpKind = tempData[PageModelToastExtensions.CaughtUpKindKey] as string;

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
                UndoStoppedSeriesId = Id(tempData, PageModelToastExtensions.UndoStoppedSeriesKey)
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

    private static string? Text(ITempDataDictionary tempData, string key) =>
        tempData[key] is string text && !string.IsNullOrWhiteSpace(text) ? text : null;

    /// <summary>A TheTVDB id written as a string (the cookie TempData serializer keeps few types); null when absent or not a number.</summary>
    private static string? Id(ITempDataDictionary tempData, string key) =>
        tempData[key] is string value && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? value : null;

    private static UndoWatchedForm? UndoWatchedFrom(ITempDataDictionary tempData)
    {
        var seriesId = Id(tempData, PageModelToastExtensions.UndoWatchedSeriesKey);
        var stamp = tempData[PageModelToastExtensions.UndoWatchedStampKey] as string;
        var stoppedStamp = tempData[PageModelToastExtensions.UndoWatchedStoppedKey] as string;

        if (seriesId is null || !long.TryParse(stamp, out _))
            return null;

        return new UndoWatchedForm(seriesId, stamp!, long.TryParse(stoppedStamp, out _) ? stoppedStamp : null);
    }
}

/// <summary>What the Undo of a "mark watched" posts back: the batch, and the stopped date to restore when the mark had resumed the series.</summary>
public sealed record UndoWatchedForm(string SeriesId, string Stamp, string? StoppedStamp);

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

    /// <summary>Whether the toast has something to press besides its close button. A new action must be counted here.</summary>
    public bool HasAction => RateSeriesId is not null || UndoWatched is not null || UndoStoppedSeriesId is not null;

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
