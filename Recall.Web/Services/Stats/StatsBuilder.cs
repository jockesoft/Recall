using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Services.Stats;

/// <summary>What <see cref="StatsBuilder"/> computes from: one user's rows and the cached metadata of what they watched.</summary>
/// <param name="Series">Cached aggregates by TheTVDB id; a series that is not cached is simply absent.</param>
/// <param name="Movies">Cached aggregates by TheTVDB id; a movie that is not cached is simply absent.</param>
/// <param name="TrackedSeriesIds">The series in the user's library (for "series finished").</param>
/// <param name="RatingCounts">How many titles got each rating, keyed by the rating (1–10).</param>
public sealed record StatsInput(
    IReadOnlyList<EpisodeWatchRecord> EpisodeWatches,
    IReadOnlyList<MovieWatch> MovieWatches,
    IReadOnlyDictionary<int, SeriesAggregate> Series,
    IReadOnlyDictionary<int, MovieAggregate> Movies,
    IReadOnlyCollection<int> TrackedSeriesIds,
    IReadOnlyDictionary<int, int> RatingCounts);

/// <summary>
/// Computes the Stats page. Pure: no clock, no database, no cache.
/// <para>
/// Two rules run through it. <b>Totals and top lists count every watch row.</b>
/// <b>The monthly chart counts only rows whose date can be trusted</b>
/// (<see cref="WatchSourceExtensions.IsDated"/>: Single and Unknown): a season
/// marked in one go, or an import, says when the user caught up, and would
/// otherwise show as one enormous day.
/// </para>
/// </summary>
public static class StatsBuilder
{
    public const int TopSeriesCount = 5;
    public const int TopGenreCount = 6;

    /// <summary>
    /// Labels TheTVDB files under genres that say what <i>format</i> a title has,
    /// not what it is about. They are left out of the genre statistics (a
    /// mini-series is a crime drama or a war story first), and only there: the
    /// chips on Series Details still show them. Add others here.
    /// </summary>
    public static readonly IReadOnlySet<string> FormatLabels =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Mini-Series" };

    public static UserStats Build(StatsInput input, DateOnly today, StatsWindow window)
    {
        var months = Enumerable.Range(0, window.Months)
            .Select(i => window.FirstMonth.AddMonths(i))
            .ToDictionary(m => m, _ => new MonthTally());

        var bySeries = new Dictionary<int, SeriesTally>();
        var byGenre = new Dictionary<string, GenreTally>(StringComparer.OrdinalIgnoreCase);
        var episodeLookups = new Dictionary<int, Dictionary<int, EpisodeSummary>>();

        long totalMinutes = 0;
        int undatedEpisodes = 0, undatedMovies = 0;
        int episodesWithoutRuntime = 0, moviesWithoutRuntime = 0;
        var notCached = new HashSet<(bool IsMovie, int Id)>();
        int titles = 0, titlesWithGenres = 0;

        // ---- episodes ----
        foreach (var watch in input.EpisodeWatches)
        {
            var minutes = 0;

            if (input.Series.TryGetValue(watch.SeriesTvdbId, out var series))
            {
                if (!episodeLookups.TryGetValue(series.TvdbId, out var lookup))
                {
                    // An aggregate can list an episode twice; the first one wins.
                    lookup = new Dictionary<int, EpisodeSummary>();
                    foreach (var episode in series.Episodes)
                        lookup.TryAdd(episode.Id, episode);
                    episodeLookups[series.TvdbId] = lookup;
                }

                // The episode's own runtime, else the series' average (which is
                // also what an episode missing from the aggregate gets).
                lookup.TryGetValue(watch.EpisodeTvdbId, out var summary);
                minutes = summary?.RuntimeMinutes is > 0
                    ? summary.RuntimeMinutes.Value
                    : series.AverageRuntimeMinutes is > 0 ? series.AverageRuntimeMinutes.Value : 0;

                if (minutes == 0)
                    episodesWithoutRuntime++;
            }
            else
            {
                notCached.Add((false, watch.SeriesTvdbId));
            }

            totalMinutes += minutes;

            if (!bySeries.TryGetValue(watch.SeriesTvdbId, out var tally))
                bySeries[watch.SeriesTvdbId] = tally = new SeriesTally();
            tally.Minutes += minutes;
            tally.Episodes++;

            if (!watch.Source.IsDated())
                undatedEpisodes++;
            else if (months.TryGetValue(MonthOf(watch.WatchedUtc), out var month))
            {
                month.Minutes += minutes;
                month.Episodes++;
            }
        }

        foreach (var (seriesId, tally) in bySeries)
        {
            titles++;
            if (input.Series.TryGetValue(seriesId, out var series) && AddToGenres(byGenre, series.Genres, tally.Minutes))
                titlesWithGenres++;
        }

        // ---- movies ----
        foreach (var watch in input.MovieWatches)
        {
            var minutes = 0;
            titles++;

            if (input.Movies.TryGetValue(watch.MovieTvdbId, out var movie))
            {
                minutes = movie.RuntimeMinutes is > 0 ? movie.RuntimeMinutes.Value : 0;
                if (minutes == 0)
                    moviesWithoutRuntime++;

                if (AddToGenres(byGenre, movie.Genres, minutes))
                    titlesWithGenres++;
            }
            else
            {
                notCached.Add((true, watch.MovieTvdbId));
            }

            totalMinutes += minutes;

            if (!watch.Source.IsDated())
                undatedMovies++;
            else if (months.TryGetValue(MonthOf(watch.WatchedUtc), out var month))
            {
                month.Minutes += minutes;
                month.Movies++;
            }
        }

        var topSeries = bySeries
            .Where(kv => input.Series.ContainsKey(kv.Key))   // a name is needed to list it
            .Select(kv => new StatsSeriesRow(kv.Key, input.Series[kv.Key].Name, Clamp(kv.Value.Minutes), kv.Value.Episodes))
            .OrderByDescending(r => r.Minutes)
            .ThenByDescending(r => r.Episodes)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.TvdbId)
            .Take(TopSeriesCount)
            .ToList();

        var topGenres = byGenre
            .Where(kv => kv.Value.Minutes > 0)
            .Select(kv => new StatsGenreRow(kv.Value.Name, Clamp(kv.Value.Minutes), kv.Value.Titles))
            .OrderByDescending(r => r.Minutes)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Take(TopGenreCount)
            .ToList();

        return new UserStats(
            new StatsTotals(
                Clamp(totalMinutes),
                input.EpisodeWatches.Count,
                input.MovieWatches.Count,
                CountFinishedSeries(input, today)),
            months
                .OrderBy(kv => kv.Key)
                .Select(kv => new StatsMonth(kv.Key, Clamp(kv.Value.Minutes), kv.Value.Episodes, kv.Value.Movies))
                .ToList(),
            new StatsUndated(undatedEpisodes, undatedMovies),
            topSeries,
            topGenres,
            new StatsGenreCoverage(titlesWithGenres, titles),
            BuildRatings(input.RatingCounts),
            new StatsGaps(notCached.Count, episodesWithoutRuntime, moviesWithoutRuntime));
    }

    /// <summary>
    /// Tracked series that are finished by the Library's rule
    /// (<see cref="SeriesLibraryStateRule"/>): ended, started, and with no aired
    /// regular episode left. The same series the Library lists under Watched.
    /// </summary>
    private static int CountFinishedSeries(StatsInput input, DateOnly today)
    {
        if (input.TrackedSeriesIds.Count == 0)
            return 0;

        var watchedIds = input.EpisodeWatches.Select(w => w.EpisodeTvdbId).ToHashSet();
        var finished = 0;

        foreach (var seriesId in input.TrackedSeriesIds.Distinct())
        {
            if (!input.Series.TryGetValue(seriesId, out var series))
                continue;

            var progress = WatchProgressCalculator.Build(seriesId, series.ToWatchableEpisodes(), watchedIds, today);

            if (SeriesLibraryStateRule.Of(series, progress) == SeriesLibraryState.Finished)
                finished++;
        }

        return finished;
    }

    /// <summary>
    /// Adds a title's minutes to each of its genres, in full: a two-hour crime
    /// drama is two hours of Crime and two hours of Drama, so the genres do not
    /// add up to the total. <see cref="FormatLabels"/> are skipped. Returns
    /// whether the title has any genre listed at all (a format label included:
    /// its genres are loaded, which is what the coverage note is about).
    /// </summary>
    private static bool AddToGenres(Dictionary<string, GenreTally> byGenre, IReadOnlyList<string> genres, long minutes)
    {
        var any = false;

        foreach (var genre in genres.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            any = true;
            if (FormatLabels.Contains(genre.Trim()))
                continue;

            if (!byGenre.TryGetValue(genre, out var tally))
                byGenre[genre] = tally = new GenreTally(genre.Trim());
            tally.Minutes += minutes;
            tally.Titles++;
        }

        return any;
    }

    private static StatsRatings BuildRatings(IReadOnlyDictionary<int, int> counts)
    {
        var byValue = new int[10];
        long sum = 0;
        var count = 0;

        foreach (var (value, n) in counts)
        {
            if (value is < 1 or > 10 || n <= 0)
                continue;

            byValue[value - 1] = n;
            sum += (long)value * n;
            count += n;
        }

        return count == 0 ? StatsRatings.Empty : new StatsRatings(byValue, count, (double)sum / count);
    }

    /// <summary>The UTC calendar month a watch falls in, like every other date in the app.</summary>
    private static DateOnly MonthOf(DateTime watchedUtc)
    {
        var utc = watchedUtc.Kind == DateTimeKind.Local ? watchedUtc.ToUniversalTime() : watchedUtc;
        return new DateOnly(utc.Year, utc.Month, 1);
    }

    private static int Clamp(long minutes) => (int)Math.Min(minutes, int.MaxValue);

    private sealed class MonthTally
    {
        public long Minutes;
        public int Episodes;
        public int Movies;
    }

    private sealed class SeriesTally
    {
        public long Minutes;
        public int Episodes;
    }

    private sealed class GenreTally(string name)
    {
        public string Name { get; } = name;
        public long Minutes;
        public int Titles;
    }
}
