namespace Recall.Web.Pages.Shared;

/// <summary>
/// Drives the shared "_UpcomingEpisodeCard" partial — a rounded card for one
/// upcoming broadcast: a left accent stripe (green when the viewer is caught up
/// on the series, amber otherwise), the series poster, series name, the
/// season/episode code with the episode title below it, the air date (with
/// weekday) top-right, and a PREMIERE / FINALE badge along the bottom. The whole
/// card links to the episode. Several episodes of one series on the same date
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

    /// <summary>Whole days from today until the episode airs (never negative).</summary>
    public int DaysUntilAired =>
        Math.Max(0, AiredDate.DayNumber - DateOnly.FromDateTime(DateTime.Today).DayNumber);
}
