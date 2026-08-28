namespace Recall.Web.Services.WatchTracking;

/// <summary>
/// Pure watch-progress logic. Everything here is deterministic given its inputs
/// (including an explicit <c>today</c>), so it can be unit-tested without mocking
/// time or TheTVDB.
/// </summary>
public static class WatchProgressCalculator
{
    /// <summary>Season/episode order, with a stable id tie-break.</summary>
    public static IReadOnlyList<WatchableEpisode> Order(IEnumerable<WatchableEpisode> episodes) =>
        episodes
            .OrderBy(e => e.SeasonNumber ?? int.MaxValue)
            .ThenBy(e => e.EpisodeNumber ?? int.MaxValue)
            .ThenBy(e => e.Id)
            .ToList();

    public static SeriesWatchProgress Build(
        int seriesTvdbId,
        IEnumerable<WatchableEpisode> episodes,
        IReadOnlySet<int> watchedEpisodeIds,
        DateOnly today)
    {
        var ordered = Order(episodes);
        var released = ordered.Where(e => e.HasAiredBy(today)).ToList();

        return new SeriesWatchProgress
        {
            SeriesTvdbId = seriesTvdbId,
            OrderedEpisodes = ordered,
            WatchedEpisodeIds = watchedEpisodeIds,
            NextUnwatchedEpisode = released.FirstOrDefault(e => !watchedEpisodeIds.Contains(e.Id)),
            UnwatchedReleasedCount = released.Count(e => !watchedEpisodeIds.Contains(e.Id)),
        };
    }

    /// <summary>Unwatched episodes strictly before <paramref name="episodeTvdbId"/> in order.</summary>
    public static int CountPriorUnwatched(
        IReadOnlyList<WatchableEpisode> orderedEpisodes,
        IReadOnlySet<int> watchedEpisodeIds,
        int episodeTvdbId)
    {
        var index = IndexOf(orderedEpisodes, episodeTvdbId);
        return index <= 0
            ? 0
            : orderedEpisodes.Take(index).Count(e => !watchedEpisodeIds.Contains(e.Id));
    }

    /// <summary>
    /// Episode ids from the first episode through <paramref name="episodeTvdbId"/>
    /// (inclusive). If the id isn't in the list, returns just that id.
    /// </summary>
    public static IReadOnlyList<int> IdsThrough(
        IReadOnlyList<WatchableEpisode> orderedEpisodes,
        int episodeTvdbId)
    {
        var index = IndexOf(orderedEpisodes, episodeTvdbId);
        return index < 0
            ? [episodeTvdbId]
            : orderedEpisodes.Take(index + 1).Select(e => e.Id).ToList();
    }

    private static int IndexOf(IReadOnlyList<WatchableEpisode> episodes, int episodeTvdbId)
    {
        for (var i = 0; i < episodes.Count; i++)
        {
            if (episodes[i].Id == episodeTvdbId)
                return i;
        }

        return -1;
    }
}
