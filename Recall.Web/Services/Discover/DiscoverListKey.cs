using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb;

namespace Recall.Web.Services.Discover;

/// <summary>
/// One Discover list: its name in <c>discover_list</c>, which filter request
/// fills it, and how many titles of the answer (after the quality filter) are
/// kept. A genre list keeps the whole page of 500, so a genre row can put
/// recent titles first from the all-time order at no extra request.
/// </summary>
public sealed record DiscoverListKey(string Key, DiscoverKind Kind, TitleFilter Filter, int Keep)
{
    /// <summary>A page of the filter: the most a list can hold.</summary>
    public const int PageSize = 500;

    /// <summary>What an ordered list (a year's most popular, all time) keeps: a row shows 20, after leaving out what the user knows.</summary>
    public const int OrderedKeep = 150;

    /// <summary>This year's series, latest premiere first: "New series" picks the last 90 days from it.</summary>
    public static DiscoverListKey SeriesNew(int year) =>
        new($"series-new-{year}", DiscoverKind.Series, new TitleFilter(TitleFilterSort.FirstAired, Year: year), PageSize);

    /// <summary>A year's series, most popular first: "Popular from {year}".</summary>
    public static DiscoverListKey SeriesPopular(int year) =>
        new($"series-popular-{year}", DiscoverKind.Series, new TitleFilter(TitleFilterSort.Score, Year: year), OrderedKeep);

    /// <summary>A year's movies, most popular first: "Movies from {year}".</summary>
    public static DiscoverListKey MoviesOfYear(int year) =>
        new($"movie-year-{year}", DiscoverKind.Movie, new TitleFilter(TitleFilterSort.Score, Year: year), OrderedKeep);

    /// <summary>The most popular series of all time: half of "All-time favourites".</summary>
    public static DiscoverListKey SeriesAllTime { get; } =
        new("series-alltime", DiscoverKind.Series, new TitleFilter(TitleFilterSort.Score), OrderedKeep);

    /// <summary>The most popular movies of all time: the other half of "All-time favourites".</summary>
    public static DiscoverListKey MoviesAllTime { get; } =
        new("movie-alltime", DiscoverKind.Movie, new TitleFilter(TitleFilterSort.Score), OrderedKeep);

    /// <summary>A genre's most popular series of all time: half of "Popular in {genre}".</summary>
    public static DiscoverListKey SeriesGenre(int genreId) =>
        new($"series-genre-{genreId}", DiscoverKind.Series, new TitleFilter(TitleFilterSort.Score, GenreId: genreId), PageSize);

    /// <summary>A genre's most popular movies of all time: the other half of "Popular in {genre}".</summary>
    public static DiscoverListKey MoviesGenre(int genreId) =>
        new($"movie-genre-{genreId}", DiscoverKind.Movie, new TitleFilter(TitleFilterSort.Score, GenreId: genreId), PageSize);

    /// <summary>
    /// The lists every user's page can need on <paramref name="today"/>, the
    /// genre lists aside: this year's (and early in the year last year's, for
    /// the rows that fall back to it), and all time.
    /// </summary>
    public static IReadOnlyList<DiscoverListKey> Fixed(DateOnly today, DiscoverOptions options)
    {
        var year = today.Year;
        var keys = new List<DiscoverListKey> { SeriesNew(year), SeriesPopular(year), MoviesOfYear(year), SeriesAllTime, MoviesAllTime };

        // "New series" reaches back NewSeriesDays: into last year until about April.
        if (today.AddDays(-Math.Max(0, options.NewSeriesDays)).Year < year)
            keys.Add(SeriesNew(year - 1));

        // "Popular from" and "Movies from" fall back to last year while this
        // year has too little; in the second half of the year it never does.
        if (today.Month <= DiscoverYear.LastFallbackMonth)
        {
            keys.Add(SeriesPopular(year - 1));
            keys.Add(MoviesOfYear(year - 1));
        }

        return keys;
    }

    /// <summary>The two lists of a "Popular in …" row.</summary>
    public static IReadOnlyList<DiscoverListKey> ForGenre(int genreId) => [SeriesGenre(genreId), MoviesGenre(genreId)];
}

/// <summary>Which year "Popular from {year}" and "Movies from {year}" are about.</summary>
public static class DiscoverYear
{
    /// <summary>The last month in which a year row may still show last year (June).</summary>
    public const int LastFallbackMonth = 6;
}
