namespace Recall.Web.Pages.Shared;

/// <summary>
/// Drives the shared "_CatchUpCard" partial — one tile in a "Catch up" grid.
/// Shows the next episode's still image with the show title, season/episode
/// code, and episode name overlaid bottom-left, plus a circular "mark watched"
/// button bottom-right. Drop a run of these inside
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
    /// Still image for the next episode. Null or blank renders a placeholder.
    /// </summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// Page handler the "mark watched" button posts to. It is called with route
    /// values <c>seriesId</c> and <c>episodeId</c>, so the host page needs a
    /// matching <c>OnPost{Handler}Async(int seriesId, int episodeId, ...)</c>.
    /// </summary>
    public string MarkWatchedHandler { get; init; } = "MarkWatched";

    /// <summary>"S02 &bull; E06" style code, or null when neither number is known.</summary>
    public string? SlateCode =>
        SeasonNumber is null && EpisodeNumber is null
            ? null
            : $"S{SeasonNumber?.ToString("D2") ?? "--"} • E{EpisodeNumber?.ToString("D2") ?? "--"}";
}
