namespace Recall.Web.Pages.Shared;

/// <summary>
/// Drives the shared "_CatchUpCard" partial — one tile in the Dashboard's "Continue watching" grid.
/// Shows the next episode's still image with the show title, season/episode
/// code, and episode name overlaid bottom-left, plus a circular "mark watched"
/// button bottom-right and a "⋯" menu top-right ("Stop watching"). Drop a run of these inside
/// <c>&lt;div class="tvdb-catchup-grid"&gt;</c>.
/// </summary>
public sealed class CatchUpCardModel
{
    public required int SeriesId { get; init; }

    public required string SeriesName { get; init; }

    public required int EpisodeId { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    public required string EpisodeName { get; init; }

    /// <summary>
    /// A 16:9 image: the next episode's still, or the series' background art.
    /// Null or blank renders a dark placeholder.
    /// </summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// Page handler the "mark watched" button posts to. It is called with route
    /// values <c>seriesId</c> and <c>episodeId</c>, so the host page needs a
    /// matching <c>OnPost{Handler}Async(int seriesId, int episodeId, ...)</c>.
    /// </summary>
    public string MarkWatchedHandler { get; init; } = "MarkWatched";

    /// <summary>
    /// Page handler the menu's "Stop watching" posts to, with the route value
    /// <c>seriesId</c>: the host page needs <c>OnPost{Handler}Async(int seriesId, ...)</c>.
    /// </summary>
    public string StopWatchingHandler { get; init; } = "StopWatching";

    /// <summary>
    /// The first of the queue: on a phone it is the one full card, with a small
    /// "Up next" label; the others are compact rows (_CatchUpRow).
    /// </summary>
    public bool IsUpNext { get; init; }

    /// <summary>
    /// Whether the list is expanded: posted back with every action (the
    /// "all" field), so the page returns as it was. tvdb-dashboard.js keeps
    /// the field in step when the list is expanded in place.
    /// </summary>
    public bool ShowAll { get; init; }

    /// <summary>The series' place in the list, 1-based: what the card's and the row's ids name.</summary>
    public required int Position { get; init; }

    /// <summary>
    /// The last of the list: its forms say so (<c>last</c>), so that when the
    /// series leaves the list the page returns to the new last one
    /// (<see cref="ListPosition.ReturnTo"/>).
    /// </summary>
    public bool IsLast { get; init; }

    /// <summary>The card's id ("catchup-card-5"): by place, not series, so a mark returns to the same place in the list.</summary>
    public string CardId => ListPosition.Id(CardPrefix, Position);

    /// <summary>The compact row's id ("catchup-row-5"), by place like <see cref="CardId"/>.</summary>
    public string RowId => ListPosition.Id(RowPrefix, Position);

    public const string CardPrefix = "catchup-card";
    public const string RowPrefix = "catchup-row";

    /// <summary>The id of the card's menu button, unique on the page (one card per series).</summary>
    public string MenuId => $"catchUpMenu{SeriesId}";

    /// <summary>The id of the compact row's menu button (the row and the card are both on the page).</summary>
    public string RowMenuId => $"catchUpRowMenu{SeriesId}";

    /// <summary>"S08E10", the row's code; null when neither number is known.</summary>
    public string? ShortCode =>
        SeasonNumber is null && EpisodeNumber is null
            ? null
            : $"S{SeasonNumber?.ToString("D2") ?? "??"}E{EpisodeNumber?.ToString("D2") ?? "??"}";

    /// <summary>"S02 &bull; E06" style code, or null when neither number is known.</summary>
    public string? SlateCode =>
        SeasonNumber is null && EpisodeNumber is null
            ? null
            : $"S{SeasonNumber?.ToString("D2") ?? "--"} • E{EpisodeNumber?.ToString("D2") ?? "--"}";
}
