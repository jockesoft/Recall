using System.Globalization;
using System.Text;

namespace Recall.Web.Infrastructure.External.TheTvDb;

/// <summary>How a filter list is ordered: TheTVDB's score (highest first) or the first air date (latest first).</summary>
public enum TitleFilterSort
{
    Score,

    /// <summary>Series only: the movie filter cannot order by date.</summary>
    FirstAired
}

/// <summary>
/// A query of TheTVDB's filter endpoints (<c>series/filter</c>,
/// <c>movies/filter</c>), always for English-original titles (<c>lang=eng</c>)
/// and from every country. One request answers one page of 500 base records,
/// the first page of the order asked for; Discover never asks for another.
/// </summary>
/// <param name="Year">Only titles of that year (TheTVDB's own idea of a title's year; for movies loose).</param>
/// <param name="GenreId">Only titles with that genre, by TheTVDB's id (<c>TheTvDbGenres</c>).</param>
public sealed record TitleFilter(TitleFilterSort Sort, int? Year = null, int? GenreId = null)
{
    /// <summary>The original language the filter asks for. Discover shows English-original titles only.</summary>
    public const string Language = "eng";

    /// <summary>The path and query of the series filter: "series/filter?lang=eng&amp;sort=score&amp;sortType=desc&amp;year=2026".</summary>
    public string SeriesPath() => Path("series", sortType: true);

    /// <summary>
    /// The path and query of the movie filter, which has no <c>sortType</c>
    /// (score comes highest first anyway) and cannot sort by date.
    /// </summary>
    public string MoviesPath()
    {
        if (Sort != TitleFilterSort.Score)
            throw new InvalidOperationException("TheTVDB's movie filter can only be ordered by score.");

        return Path("movies", sortType: false);
    }

    private string Path(string resource, bool sortType)
    {
        var query = new StringBuilder(resource).Append("/filter?lang=").Append(Language)
            .Append("&sort=").Append(Sort == TitleFilterSort.Score ? "score" : "firstAired");

        if (sortType)
            query.Append("&sortType=desc");
        if (Year is { } year)
            query.Append("&year=").Append(year.ToString(CultureInfo.InvariantCulture));
        if (GenreId is { } genre)
            query.Append("&genre=").Append(genre.ToString(CultureInfo.InvariantCulture));

        return query.ToString();
    }
}
