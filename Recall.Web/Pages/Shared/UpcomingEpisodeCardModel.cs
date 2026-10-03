using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Pages.Shared;

/// <summary>
/// Drives the shared "_UpcomingEpisodeCard" partial — a compact card for one
/// upcoming broadcast: a left accent stripe (green when the viewer is caught up
/// on the series, amber otherwise), a small poster, the series name, the
/// season/episode code with the episode title, a PREMIERE / FINALE badge, and
/// last the air date with its countdown as a secondary line. The whole card
/// links to the episode. Several episodes of one series on the same date
/// collapse into a single card via <see cref="EpisodeFrom"/>/<see cref="EpisodeTo"/>.
/// </summary>
public sealed class UpcomingEpisodeCardModel
{
    public required int SeriesId { get; init; }

    public required string SeriesName { get; init; }

    /// <summary>Episode the card links to — the first episode when collapsed.</summary>
    public required int LinkEpisodeId { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeFrom { get; init; }

    public int? EpisodeTo { get; init; }

    /// <summary>
    /// Episode title. Hidden when it is blank or a "TBA" placeholder, and never
    /// shown for a collapsed multi-episode card.
    /// </summary>
    public string? EpisodeName { get; init; }

    public string? ImageUrl { get; init; }

    public required DateOnly AiredDate { get; init; }

    /// <summary>When it is released: the card's date and time, and its countdown.</summary>
    public required ReleaseMoment Release { get; init; }

    /// <summary>Season premiere (episode 1) — renders the PREMIERE badge.</summary>
    public bool IsPremiere { get; init; }

    /// <summary>Season or series finale — renders the FINALE badge.</summary>
    public bool IsFinale { get; init; }

    /// <summary>How many episodes this card stands in for (1 unless several were collapsed).</summary>
    public int EpisodeCount { get; init; } = 1;

    /// <summary>Episodes beyond the linked one — drives the discreet "+N more" tag.</summary>
    public int ExtraEpisodeCount => Math.Max(0, EpisodeCount - 1);

    /// <summary>Viewer has watched every aired episode of this series (green stripe).</summary>
    public bool SeriesCaughtUp { get; init; }

    /// <summary>"S02 &bull; E06" or "S02 &bull; E01-E08"; null when no numbers are known.</summary>
    public string? SlateCode
    {
        get
        {
            if (SeasonNumber is null && EpisodeFrom is null)
                return null;

            var s = SeasonNumber?.ToString("D2") ?? "--";
            if (EpisodeFrom is null)
                return $"S{s} • E--";
            return EpisodeTo is null || EpisodeTo == EpisodeFrom
                ? $"S{s} • E{EpisodeFrom.Value:D2}"
                : $"S{s} • E{EpisodeFrom.Value:D2}-E{EpisodeTo.Value:D2}";
        }
    }

    /// <summary>Episode title to render, or null when it should be hidden.</summary>
    public string? DisplayEpisodeName =>
        string.IsNullOrWhiteSpace(EpisodeName)
            || string.Equals(EpisodeName.Trim(), "TBA", StringComparison.OrdinalIgnoreCase)
                ? null
                : EpisodeName.Trim();

    /// <summary>The page's "today" (UTC), passed in so the card doesn't read a clock of its own.</summary>
    public required DateOnly Today { get; init; }

    /// <summary>
    /// False under a "Today" or "Tomorrow" heading, which already says when:
    /// the card then shows no date and no countdown.
    /// </summary>
    public bool ShowDate { get; init; } = true;

    /// <summary>
    /// Whole days from today until the episode is released (never negative),
    /// counted in UTC dates like the "Today" / "Tomorrow" headings.
    /// </summary>
    public int DaysUntilAired =>
        Math.Max(0, Release.GroupDate.DayNumber - Today.DayNumber);

    /// <summary>"in 3 days"; "today" and "tomorrow" for the first two.</summary>
    public string Countdown => DaysUntilAired switch
    {
        0 => "today",
        1 => "tomorrow",
        var days => $"in {days} days"
    };
}
