using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Services.WatchTracking;

/// <summary>
/// The one order for "series I am in the middle of": the Dashboard's Continue
/// watching cards and the Library's Watching section. Nothing else should sort
/// those lists.
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

    /// <summary>When each tracked series was added to the library, keyed by its TheTVDB id.</summary>
    public static IReadOnlyDictionary<int, DateTime> AddedUtc(IEnumerable<TrackedSeries> trackedSeries) =>
        trackedSeries
            .GroupBy(s => s.TvdbId)
            .ToDictionary(g => g.Key, g => g.Max(s => s.CreatedUtc));
}
