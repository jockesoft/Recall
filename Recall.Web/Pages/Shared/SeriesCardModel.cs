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
}
