using System.Globalization;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Web.Services.Discover;

/// <summary>One row of the Discover page: a heading, a line under it, and its titles in order.</summary>
/// <param name="Slug">Letters and hyphens only ("new", "genre-crime"): the cards are named by it and their place ("discover-new-5").</param>
public sealed record DiscoverRow(string Slug, string Heading, string Description, IReadOnlyList<DiscoverTitle> Titles);

/// <summary>What the Discover page shows.</summary>
/// <param name="HasLists">Whether the daily job has stored any list yet: without one the page says suggestions are on their way.</param>
public sealed record DiscoverPage(IReadOnlyList<DiscoverRow> Rows, bool HasLists)
{
    public static DiscoverPage NotYetFetched { get; } = new([], false);
}

/// <summary>What the rows are built from: the stored lists, what the user knows (and hid), and their genres.</summary>
public sealed record DiscoverInput(
    IReadOnlyDictionary<string, DiscoverList> Lists,
    KnownTitles Known,
    IReadOnlyList<DiscoverGenre> Genres);

/// <summary>
/// The Discover rows, decided in one place from data already stored (no
/// request to TheTVDB): which titles, in which order, under which heading.
/// In page order, each row leaves out every title the user already knows and
/// every title an earlier row shows, and is left out itself below
/// <see cref="DiscoverOptions.MinPerRow"/> titles:
/// <list type="number">
/// <item>New series: premiered in the last <see cref="DiscoverOptions.NewSeriesDays"/> days, most popular first.</item>
/// <item>Popular from {year}: this year's series by score, or last year's early in the year (<see cref="ChooseYear"/>).</item>
/// <item>Popular in {genre}, for each of the user's top two genres: series and movies together, the last
/// <see cref="DiscoverOptions.RecentYears"/> years first, by score within each band. When there is none to show,
/// one "All-time favourites" row takes their place.</item>
/// <item>Movies from {year}: released movies of the year, by score, with the same year rule.</item>
/// </list>
/// </summary>
public static class DiscoverBuilder
{
    public const string NewSlug = "new";
    public const string PopularSlug = "popular";
    public const string FavouritesSlug = "favourites";
    public const string MoviesSlug = "movies";

    /// <summary>The lists a user's page reads on <paramref name="today"/>: the fixed ones and their genres'.</summary>
    public static IReadOnlyList<string> KeysFor(DateOnly today, IReadOnlyList<DiscoverGenre> genres, DiscoverOptions options) =>
        DiscoverListKey.Fixed(today, options)
            .Concat(genres.SelectMany(g => DiscoverListKey.ForGenre(g.Id)))
            .Select(k => k.Key)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static DiscoverPage Build(DiscoverInput input, DateOnly today, DiscoverOptions options)
    {
        if (input.Lists.Count == 0)
            return DiscoverPage.NotYetFetched;

        var rows = new List<DiscoverRow>();
        var shown = new HashSet<(DiscoverKind, int)>();

        // Leaves out what the user knows and what an earlier row shows.
        IEnumerable<DiscoverTitle> Fresh(IEnumerable<DiscoverTitle> titles) =>
            titles.Where(t => !input.Known.Contains(t.Kind, t.TvdbId) && !shown.Contains((t.Kind, t.TvdbId)))
                .DistinctBy(t => (t.Kind, t.TvdbId));

        void Add(string slug, string heading, string description, IEnumerable<DiscoverTitle> candidates)
        {
            var titles = candidates.Take(Math.Max(1, options.MaxPerRow)).ToList();
            if (titles.Count < Math.Max(1, options.MinPerRow))
                return;

            rows.Add(new DiscoverRow(slug, heading, description, titles));
            foreach (var title in titles)
                shown.Add((title.Kind, title.TvdbId));
        }

        var year = today.Year;

        // 1. New series.
        var since = today.AddDays(-Math.Max(0, options.NewSeriesDays));
        Add(NewSlug, "New series",
            $"Premiered in the last {options.NewSeriesDays} days, the most popular first.",
            Fresh(Titles(input, DiscoverListKey.SeriesNew(year).Key)
                    .Concat(Titles(input, DiscoverListKey.SeriesNew(year - 1).Key))
                    .Where(t => t.FirstAired is { } aired && aired >= since && aired <= today))
                .OrderByDescending(t => t.Score).ThenByDescending(t => t.FirstAired).ThenBy(t => t.Name, StringComparer.Ordinal));

        // 2. Popular from {year}: premiered by today (a high score for a series
        // still to come is anticipation, not something to watch).
        IEnumerable<DiscoverTitle> PopularSeries(int y) =>
            Fresh(Titles(input, DiscoverListKey.SeriesPopular(y).Key)
                .Where(t => t.FirstAired is { } aired && aired <= today));

        var popularYear = ChooseYear(year, y => PopularSeries(y).Count(), options);
        Add(PopularSlug, $"Popular from {popularYear.ToString(CultureInfo.InvariantCulture)}",
            $"Series that premiered in {popularYear.ToString(CultureInfo.InvariantCulture)}, the most popular first.",
            PopularSeries(popularYear));

        // 3. Popular in {genre}, or All-time favourites.
        var genreRows = 0;
        foreach (var genre in input.Genres)
        {
            var series = DiscoverListKey.SeriesGenre(genre.Id).Key;
            var movies = DiscoverListKey.MoviesGenre(genre.Id).Key;
            if (!input.Lists.ContainsKey(series) && !input.Lists.ContainsKey(movies))
                continue; // not fetched yet: the job fetches a new genre's lists on its next daily run

            var before = rows.Count;
            Add(GenreSlug(genre.Name), $"Popular in {genre.Name}",
                $"{genre.Name} series and movies, the last {options.RecentYears} years first.",
                ByYearBands(Fresh(Titles(input, series).Concat(Titles(input, movies))), today, options.RecentYears));
            genreRows += rows.Count - before;
        }

        if (genreRows == 0)
        {
            Add(FavouritesSlug, "All-time favourites",
                "The most popular series and movies on TheTVDB.",
                Fresh(Titles(input, DiscoverListKey.SeriesAllTime.Key).Concat(Titles(input, DiscoverListKey.MoviesAllTime.Key)))
                    .OrderByDescending(t => t.Score).ThenBy(t => t.Name, StringComparer.Ordinal));
        }

        // 4. Movies from {year}: released ones whose own year is the year
        // (TheTVDB's year filter also returns movies released earlier elsewhere).
        IEnumerable<DiscoverTitle> Movies(int y) =>
            Fresh(Titles(input, DiscoverListKey.MoviesOfYear(y).Key)
                .Where(t => t.Year == y && string.Equals(t.Status, "Released", StringComparison.OrdinalIgnoreCase)));

        var movieYear = ChooseYear(year, y => Movies(y).Count(), options);
        Add(MoviesSlug, $"Movies from {movieYear.ToString(CultureInfo.InvariantCulture)}",
            $"Released in {movieYear.ToString(CultureInfo.InvariantCulture)}, the most popular first.",
            Movies(movieYear));

        return new DiscoverPage(rows, HasLists: true);
    }

    /// <summary>
    /// The year a "… from {year}" row is about: this year once it can fill a
    /// row, else last year when last year has more to show (early in the year,
    /// when this year's titles have barely gathered a score; the job keeps last
    /// year's lists until June). Never both.
    /// </summary>
    public static int ChooseYear(int year, Func<int, int> available, DiscoverOptions options)
    {
        var thisYear = available(year);
        if (thisYear >= options.MaxPerRow)
            return year;

        return available(year - 1) > thisYear ? year - 1 : year;
    }

    /// <summary>
    /// A genre row's order: titles of the last <paramref name="recentYears"/>
    /// years (this one included) first, then older ones, each band by score.
    /// The lists hold a genre's 500 most popular of all time, so this costs no
    /// request.
    /// </summary>
    public static IEnumerable<DiscoverTitle> ByYearBands(IEnumerable<DiscoverTitle> titles, DateOnly today, int recentYears)
    {
        var firstRecentYear = today.Year - Math.Max(1, recentYears) + 1;

        return titles
            .OrderBy(t => t.Year >= firstRecentYear ? 0 : 1)
            .ThenByDescending(t => t.Score)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ThenBy(t => t.Kind)
            .ThenBy(t => t.TvdbId);
    }

    /// <summary>"Science Fiction" → "genre-science-fiction": letters and hyphens, as a card's id needs.</summary>
    public static string GenreSlug(string genreName)
    {
        var words = new string(genreName.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? "genre" : "genre-" + string.Join('-', words);
    }

    private static IReadOnlyList<DiscoverTitle> Titles(DiscoverInput input, string key) =>
        input.Lists.TryGetValue(key, out var list) ? list.Titles : [];
}
