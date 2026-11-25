using Recall.Web.Services.Stats;

namespace Recall.Web.Services.Discover;

/// <summary>A genre a "Popular in …" row is about: its name, as Recall stores it, and TheTVDB's id for the filter.</summary>
public sealed record DiscoverGenre(string Name, int Id);

/// <summary>
/// TheTVDB's genres (<c>/genres</c>, the same vocabulary Recall stores on a
/// series or a movie, so a Stats genre maps to a filter id by name), and the
/// rule for which of a user's genres get a row.
/// </summary>
public static class DiscoverGenres
{
    /// <summary>How many "Popular in …" rows a user gets: one for each of their top genres, up to this many.</summary>
    public const int RowsPerUser = 2;

    /// <summary>TheTVDB's genre ids by name (checked 2026-11-25; there is no 20).</summary>
    public static readonly IReadOnlyDictionary<string, int> Ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Soap"] = 1, ["Science Fiction"] = 2, ["Reality"] = 3, ["News"] = 4, ["Mini-Series"] = 5, ["Horror"] = 6,
        ["Home and Garden"] = 7, ["Game Show"] = 8, ["Food"] = 9, ["Fantasy"] = 10, ["Family"] = 11, ["Drama"] = 12,
        ["Documentary"] = 13, ["Crime"] = 14, ["Comedy"] = 15, ["Children"] = 16, ["Animation"] = 17, ["Adventure"] = 18,
        ["Action"] = 19, ["Sport"] = 21, ["Suspense"] = 22, ["Talk Show"] = 23, ["Thriller"] = 24, ["Travel"] = 25,
        ["Western"] = 26, ["Anime"] = 27, ["Romance"] = 28, ["Musical"] = 29, ["Podcast"] = 30, ["Mystery"] = 31,
        ["Indie"] = 32, ["History"] = 33, ["War"] = 34, ["Martial Arts"] = 35, ["Awards Show"] = 36
    };

    /// <summary>
    /// Genres that never get a row: what happens on air today rather than a
    /// story to discover, and the labels that say what format a title has
    /// (<see cref="StatsBuilder.FormatLabels"/>, which Stats' ranking already
    /// leaves out). Reality stays in.
    /// </summary>
    public static readonly IReadOnlySet<string> Excluded =
        new HashSet<string>(["News", "Talk Show", "Podcast", "Awards Show", .. StatsBuilder.FormatLabels], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The user's genres for the "Popular in …" rows: the first
    /// <see cref="RowsPerUser"/> of their Stats genres (ranked by watch time)
    /// that may have a row. None while fewer than
    /// <paramref name="minTitlesWithGenres"/> of their titles have genres:
    /// too little to go by, so the page shows "All-time favourites" instead.
    /// </summary>
    public static IReadOnlyList<DiscoverGenre> ForUser(UserStats stats, int minTitlesWithGenres)
    {
        if (stats.GenreCoverage.TitlesWithGenres < Math.Max(1, minTitlesWithGenres))
            return [];

        return stats.TopGenres
            .Select(genre => genre.Name.Trim())
            .Where(name => !Excluded.Contains(name) && Ids.ContainsKey(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(RowsPerUser)
            .Select(name => new DiscoverGenre(name, Ids[name]))
            .ToList();
    }
}
