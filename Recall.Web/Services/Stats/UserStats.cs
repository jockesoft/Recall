using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Services.Stats;

/// <summary>
/// Everything the Stats page shows for one user, computed by
/// <see cref="StatsBuilder"/>. Plain values only, so it can be serialised as
/// it is if the page ever needs a cache.
/// </summary>
public sealed record UserStats(
    StatsTotals Totals,
    IReadOnlyList<StatsMonth> Months,
    StatsUndated Undated,
    IReadOnlyList<StatsSeriesRow> TopSeries,
    IReadOnlyList<StatsGenreRow> TopGenres,
    StatsGenreCoverage GenreCoverage,
    StatsRatings Ratings,
    StatsGaps Gaps)
{
    public static UserStats Empty { get; } = new(
        new StatsTotals(0, 0, 0, 0),
        [],
        new StatsUndated(0, 0),
        [],
        [],
        new StatsGenreCoverage(0, 0),
        StatsRatings.Empty,
        new StatsGaps(0, 0, 0));

    /// <summary>Nothing watched and nothing rated: the page has nothing to show.</summary>
    public bool IsEmpty => Totals.Episodes == 0 && Totals.Movies == 0 && Ratings.Count == 0;

    /// <summary>Whether any month of the chart has something in it.</summary>
    public bool HasChart => Months.Any(m => m.Episodes > 0 || m.Movies > 0);

    /// <summary>The largest month, for scaling the chart's bars; 0 when the chart is empty.</summary>
    public int LargestMonthMinutes => Months.Count == 0 ? 0 : Months.Max(m => m.Minutes);
}

/// <summary>All-time figures, from every watch row whatever its source.</summary>
/// <param name="Minutes">Runtime of everything watched; a title with no known length adds nothing.</param>
/// <param name="SeriesFinished">
/// Tracked series the Library lists under Watched: ended, at least one regular
/// episode watched, and no aired regular episode left.
/// </param>
public sealed record StatsTotals(int Minutes, int Episodes, int Movies, int SeriesFinished)
{
    /// <summary>The same total in words ("1 month, 3 days"), as Profile shows it.</summary>
    public WatchTimeSummary WatchTime => new(Minutes, Episodes, Movies);
}

/// <summary>One UTC calendar month of the chart: dated watches only.</summary>
/// <param name="Month">The first day of the month.</param>
public sealed record StatsMonth(DateOnly Month, int Minutes, int Episodes, int Movies);

/// <summary>
/// What the chart leaves out: watches marked in bulk or imported, whose date is
/// when they were recorded and not when they were watched. They are all in the
/// totals and the top lists.
/// </summary>
public sealed record StatsUndated(int Episodes, int Movies)
{
    public bool Any => Episodes > 0 || Movies > 0;
}

public sealed record StatsSeriesRow(int TvdbId, string Name, int Minutes, int Episodes);

/// <param name="Titles">Series and movies of this genre the user has watched something of.</param>
public sealed record StatsGenreRow(string Name, int Minutes, int Titles);

/// <summary>How many of the watched titles have genres at all (series cached before 2026-10-02 have none until refreshed).</summary>
public sealed record StatsGenreCoverage(int TitlesWithGenres, int Titles)
{
    public bool IsComplete => TitlesWithGenres >= Titles;
}

/// <param name="CountByValue">Ten entries: index 0 is how many titles were rated 1, index 9 how many were rated 10.</param>
public sealed record StatsRatings(IReadOnlyList<int> CountByValue, int Count, double? Average)
{
    public static StatsRatings Empty { get; } = new(new int[10], 0, null);

    public int LargestCount => CountByValue.Count == 0 ? 0 : CountByValue.Max();
}

/// <summary>
/// What could not be measured, so the page can say so instead of quietly
/// showing a smaller number.
/// </summary>
/// <param name="TitlesNotCached">Watched series and movies with no cached record: counted, but with no length, name or genre.</param>
/// <param name="EpisodesWithoutRuntime">Episodes of cached series with neither a runtime of their own nor a series average.</param>
/// <param name="MoviesWithoutRuntime">Cached movies with no runtime.</param>
public sealed record StatsGaps(int TitlesNotCached, int EpisodesWithoutRuntime, int MoviesWithoutRuntime)
{
    public bool Any => TitlesNotCached > 0 || EpisodesWithoutRuntime > 0 || MoviesWithoutRuntime > 0;
}

/// <summary>The months the chart covers.</summary>
/// <param name="FirstMonth">The first day of the first month.</param>
public sealed record StatsWindow(DateOnly FirstMonth, int Months)
{
    /// <summary>The month of <paramref name="today"/> and the eleven before it.</summary>
    public static StatsWindow LastTwelveMonths(DateOnly today) =>
        new(new DateOnly(today.Year, today.Month, 1).AddMonths(-11), 12);

    /// <summary>January to December of one year (what a year-in-review page would ask for).</summary>
    public static StatsWindow CalendarYear(int year) => new(new DateOnly(year, 1, 1), 12);
}
