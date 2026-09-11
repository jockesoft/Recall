namespace Recall.Web.Pages.Shared;

/// <summary>
/// Drives the shared "_SeriesCard" partial — a single poster tile (cover image,
/// title, year) that links to Series/Details. Drop a set of these inside a
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
    /// First-aired date. Only the year is shown under the title; null hides the
    /// year line entirely.
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
    /// Optional small corner icon indicating content type, e.g. "fa-film" for a
    /// movie or "fa-tv" for a series (any Font Awesome solid icon name, without
    /// the "fa-solid" prefix). Null omits the badge entirely.
    /// </summary>
    public string? BadgeIcon { get; init; }

    /// <summary>Accessible label for <see cref="BadgeIcon"/>, e.g. "Movie" or "Series".</summary>
    public string? BadgeLabel { get; init; }

    /// <summary>
    /// Color modifier for the badge — "series" tints it amber, "movie" tints it
    /// teal (the theme's accent colors). Ignored when <see cref="BadgeIcon"/> is null.
    /// </summary>
    public string BadgeVariant { get; init; } = "series";

    /// <summary>Optional line of text under the title, e.g. "Watched on Sep 11, 2026".</summary>
    public string? Caption { get; init; }
}
