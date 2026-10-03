using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Services.WatchTracking;

/// <summary>
/// The one place that decides both the order and the grouping of "series I am
/// in the middle of": the Dashboard's Continue watching cards and the Library's
/// Watching section. Nothing else should sort or split those lists.
///
/// <para><b>Order</b> (<see cref="Order"/>):</para>
/// <list type="number">
/// <item>Series with watch activity first, the most recently watched first.
/// Activity is the latest <c>WatchedUtc</c> of any of the user's episode watches
/// for the series, specials included: a special never drives the next episode,
/// but watching one is still watching the series.</item>
/// <item>Then series with nothing watched yet, the most recently added to the
/// library first.</item>
/// <item>Ties (and a series with no known added date) by name, then by id, so
/// the order is the same on every load.</item>
/// </list>
///
/// <para><b>Grouping</b> (<see cref="Arrange"/>): a series is dormant, "haven't
/// watched in a while", when its last activity is more than
/// <see cref="LibraryOptions.DormantAfterDays"/> days ago; for a series never
/// started, when it was added that long ago. A dormant series comes back to
/// the main list while a regular season of it has premiered within
/// <see cref="LibraryOptions.PremiereReturnDays"/> days
/// (<see cref="HasRecentPremiere"/>), placed after the series with real,
/// recent activity. Watching anything brings a series back by itself. A series
/// with nothing to watch yet (no aired regular episode) is never dormant: there
/// is nothing the user has left unwatched for a while.</para>
/// </summary>
public static class ContinueWatchingOrder
{
    /// <param name="series">The series to order; anything that knows its series id and name.</param>
    /// <param name="lastWatchedUtc">Latest watch per series id (<c>IEpisodeWatchRepository.GetLastWatchedUtcBySeriesAsync</c>); a series without activity is absent.</param>
    /// <param name="addedUtc">When each series was added to the library, by series id (<see cref="AddedUtc"/>).</param>
    public static IReadOnlyList<T> Order<T>(
        IEnumerable<T> series,
        Func<T, int> seriesId,
        Func<T, string> name,
        IReadOnlyDictionary<int, DateTime> lastWatchedUtc,
        IReadOnlyDictionary<int, DateTime> addedUtc)
    {
        return series
            .Select(item =>
            {
                var id = seriesId(item);
                var hasActivity = lastWatchedUtc.TryGetValue(id, out var watched);
                var hasAdded = addedUtc.TryGetValue(id, out var added);

                return new
                {
                    Item = item,
                    Id = id,
                    HasActivity = hasActivity,
                    // One key for both groups: when it was last watched, or when it was added.
                    When = hasActivity ? watched : hasAdded ? added : DateTime.MinValue
                };
            })
            .OrderByDescending(x => x.HasActivity)
            .ThenByDescending(x => x.When)
            .ThenBy(x => name(x.Item), StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Id)
            .Select(x => x.Item)
            .ToList();
    }

    /// <summary>
    /// Splits the queue into the main list and the dormant group, each in
    /// order. The main list is: series with recent activity (by <see cref="Order"/>),
    /// then dormant series a season premiere has brought back, then series not
    /// yet started and added recently. The dormant group is ordered by
    /// <see cref="Order"/> as well.
    /// </summary>
    /// <param name="recentPremieres">Ids of series with a recent season premiere (<see cref="HasRecentPremiere"/>).</param>
    /// <param name="today">The UTC date (<c>AirDate.Today</c>).</param>
    /// <param name="nothingToWatch">
    /// Ids of series in the list that have no aired regular episode to watch
    /// (an announced series, or one with only specials): never dormant. Only
    /// the Library passes any; the Dashboard and the digest list series by
    /// their next episode, so such a series is not in their lists at all.
    /// </param>
    public static ContinueWatchingList<T> Arrange<T>(
        IEnumerable<T> series,
        Func<T, int> seriesId,
        Func<T, string> name,
        IReadOnlyDictionary<int, DateTime> lastWatchedUtc,
        IReadOnlyDictionary<int, DateTime> addedUtc,
        IReadOnlySet<int> recentPremieres,
        DateOnly today,
        LibraryOptions options,
        IReadOnlySet<int>? nothingToWatch = null)
    {
        var withActivity = new List<T>();
        var broughtBack = new List<T>();
        var notStarted = new List<T>();
        var dormant = new List<T>();

        foreach (var item in series)
        {
            var id = seriesId(item);
            var hasActivity = lastWatchedUtc.TryGetValue(id, out var watched);
            DateTime? since = hasActivity ? watched : addedUtc.TryGetValue(id, out var added) ? added : null;

            if (IsStale(since, today, options.DormantAfterDays) && nothingToWatch?.Contains(id) != true)
                (recentPremieres.Contains(id) ? broughtBack : dormant).Add(item);
            else
                (hasActivity ? withActivity : notStarted).Add(item);
        }

        IReadOnlyList<T> InOrder(List<T> items) => Order(items, seriesId, name, lastWatchedUtc, addedUtc);

        return new ContinueWatchingList<T>(
            [.. InOrder(withActivity), .. InOrder(broughtBack), .. InOrder(notStarted)],
            InOrder(dormant));
    }

    /// <summary>
    /// More than <paramref name="dormantAfterDays"/> days between
    /// <paramref name="since"/> and today, counted in UTC dates. Never when the
    /// feature is off (0 or less) or the date is unknown.
    /// </summary>
    private static bool IsStale(DateTime? since, DateOnly today, int dormantAfterDays) =>
        dormantAfterDays > 0
        && since is { } date
        && date != default
        && today.DayNumber - DateOnly.FromDateTime(date).DayNumber > dormantAfterDays;

    /// <summary>
    /// True when the first episode of a regular season was released
    /// (<see cref="EpisodeRelease"/>) within the <paramref name="days"/> days
    /// before <paramref name="nowUtc"/>; not before its release moment.
    /// Specials (season 0) have no premiere in this sense, and an ordinary
    /// episode airing is not one: a weekly show the user abandoned airs all the
    /// time and must be able to go dormant.
    /// </summary>
    public static bool HasRecentPremiere(IEnumerable<WatchableEpisode> episodes, DateTime nowUtc, int days)
    {
        if (days <= 0)
            return false;

        return episodes
            .Where(e => e.SeasonNumber is > 0 && e.EpisodeNumber.HasValue)
            .GroupBy(e => e.SeasonNumber)
            .Select(season => season.OrderBy(e => e.EpisodeNumber).ThenBy(e => e.Id).First())
            .Any(first => first.Release is { } release
                          && release.IsReleasedBy(nowUtc)
                          && nowUtc - release.Utc <= TimeSpan.FromDays(days));
    }

    /// <summary>When each tracked series was added to the library, keyed by its TheTVDB id.</summary>
    public static IReadOnlyDictionary<int, DateTime> AddedUtc(IEnumerable<TrackedSeries> trackedSeries) =>
        trackedSeries
            .GroupBy(s => s.TvdbId)
            .ToDictionary(g => g.Key, g => g.Max(s => s.CreatedUtc));
}

/// <summary>The queue, split by <see cref="ContinueWatchingOrder.Arrange"/>: the main list and the "haven't watched in a while" group, each in order.</summary>
public sealed record ContinueWatchingList<T>(IReadOnlyList<T> Active, IReadOnlyList<T> Dormant);
