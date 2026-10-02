namespace Recall.Web.Pages.Shared;

/// <summary>
/// Drives the shared "_SeriesCard" partial — a single poster tile (cover image,
/// title, optional progress, then a "Series · 2008" meta line) that links to
/// the title's details page. Drop a set of these inside a
/// <c>&lt;div class="tvdb-series-grid"&gt;</c> to get a responsive poster grid.
/// </summary>
public sealed class SeriesCardModel
{
    /// <summary>TheTVDB id — used to build the Series/Details link.</summary>
    public required int TvdbId { get; init; }

    public required string Name { get; init; }

    /// <summary>Poster/cover art URL. Null or blank renders a "No art" placeholder.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// First-aired (or release) date. Only the year is shown, in the meta line.
    /// </summary>
    public DateOnly? FirstAired { get; init; }

    /// <summary>
    /// Aired episodes the user has marked watched. With <see cref="ReleasedEpisodes"/>
    /// this drives the thin progress bar along the bottom of the poster. When it
    /// is 0 (or either value is unknown) no bar is drawn.
    /// </summary>
    public int WatchedEpisodes { get; init; }

    /// <summary>Total aired episodes — the denominator for the progress bar.</summary>
    public int ReleasedEpisodes { get; init; }

    /// <summary>
    /// When true, a heart toggle is overlaid on the top-right of the poster.
    /// The host page must define a handler named <see cref="LikeHandler"/> (it
    /// receives the series TVDB id as <c>id</c>) and pass the current state in
    /// <see cref="IsLiked"/>.
    /// </summary>
    public bool ShowLike { get; init; }

    /// <summary>Whether the current user has liked this series. Only used when <see cref="ShowLike"/> is true.</summary>
    public bool IsLiked { get; init; }

    /// <summary>Page handler the heart form posts to. Only used when <see cref="ShowLike"/> is true.</summary>
    public string LikeHandler { get; init; } = "ToggleSeriesLike";

    /// <summary>Extra hidden fields the like handler needs, rendered inside the heart form.</summary>
    public IDictionary<string, string> LikeHiddenFields { get; init; } = new Dictionary<string, string>();

    /// <summary>Noun used in the like button's tooltip/aria-label, e.g. "series" or "movie".</summary>
    public string LikeTargetNoun { get; init; } = "series";

    /// <summary>Page the poster links to. Defaults to Series/Details; pass "/Movies/Details" for a movie.</summary>
    public string DetailsPage { get; init; } = "/Series/Details";

    /// <summary>
    /// True for a movie. Decides the word that starts the meta line ("Movie" or
    /// "Series") and the card's <c>data-content-type</c>, which the All /
    /// Series / Movies filter reads. The poster itself carries no type badge.
    /// </summary>
    public bool IsMovie { get; init; }

    /// <summary>"movie" or "series".</summary>
    public string ContentType => IsMovie ? "movie" : "series";

    /// <summary>
    /// Readable progress shown under the title of a series being watched, e.g.
    /// "6 of 16 · S05" (<c>SeasonWatchProgress.Label</c>). Null shows nothing.
    /// </summary>
    public string? ProgressText { get; init; }

    /// <summary>
    /// What follows the type in the meta line, in place of the year: "watched
    /// Sep 25" gives "Movie · watched Sep 25". Null shows the year.
    /// </summary>
    public string? Caption { get; init; }

    /// <summary>"Series · 2008", "Movie · watched Sep 25", or just the type when there is nothing to add.</summary>
    public string MetaLine
    {
        get
        {
            var type = IsMovie ? "Movie" : "Series";
            var detail = !string.IsNullOrWhiteSpace(Caption) ? Caption : FirstAired?.Year.ToString();
            return string.IsNullOrWhiteSpace(detail) ? type : $"{type} · {detail}";
        }
    }
}
