namespace Recall.Web.Services.WatchTracking;

/// <summary>
/// Pure watch-progress logic. Everything here is deterministic given its inputs
/// (including an explicit <c>today</c>), so it can be unit-tested without mocking
/// time or TheTVDB.
/// </summary>
public static class WatchProgressCalculator
{
    /// <summary>
    /// Watch order: numbered seasons in season/episode order, then the specials
    /// (season 0), with a stable id tie-break. The specials are in the list so
    /// they can be shown and marked; <see cref="Build"/> leaves them out of
    /// everything it counts.
    /// </summary>
    public static IReadOnlyList<WatchableEpisode> Order(IEnumerable<WatchableEpisode> episodes) =>
        episodes
            .OrderByWatchOrder(e => e.SeasonNumber, e => e.EpisodeNumber, e => e.Id)
            .ToList();

    /// <summary>
    /// Progress is about the regular episodes only. Specials (season 0) never
    /// count: they are not in the released or watched totals and are never the
    /// next episode, so a series whose regular episodes are all watched is up
    /// to date whatever is left on its Specials tab.
    /// </summary>
    public static SeriesWatchProgress Build(
        int seriesTvdbId,
        IEnumerable<WatchableEpisode> episodes,
        IReadOnlySet<int> watchedEpisodeIds,
        DateOnly today)
    {
        var ordered = Order(episodes);
        var released = ordered.Where(e => !e.IsSpecial && e.HasAiredBy(today)).ToList();
        var watchedReleased = released.Count(e => watchedEpisodeIds.Contains(e.Id));
        var next = released.FirstOrDefault(e => !watchedEpisodeIds.Contains(e.Id));

        SeasonWatchProgress? currentSeason = null;
        if (next?.SeasonNumber is { } seasonNumber)
        {
            var season = released.Where(e => e.SeasonNumber == seasonNumber).ToList();
            currentSeason = new SeasonWatchProgress(
                seasonNumber,
                season.Count(e => watchedEpisodeIds.Contains(e.Id)),
                season.Count);
        }

        return new SeriesWatchProgress
        {
            SeriesTvdbId = seriesTvdbId,
            OrderedEpisodes = ordered,
            WatchedEpisodeIds = watchedEpisodeIds,
            NextUnwatchedEpisode = next,
            CurrentSeason = currentSeason,
            ReleasedCount = released.Count,
            WatchedReleasedCount = watchedReleased,
        };
    }

    /// <summary>
    /// The season a series page opens on: the season of the next episode to
    /// watch; when the viewer is caught up, the latest numbered season (where
    /// anything new will appear); the specials only when the series has no
    /// other season. Null when there are no episodes.
    /// </summary>
    public static int? DefaultSeason(SeriesWatchProgress progress)
    {
        if (progress.NextUnwatchedEpisode?.SeasonNumber is { } next)
            return next;

        var seasons = progress.OrderedEpisodes
            .Select(e => e.SeasonNumber)
            .Where(n => n.HasValue)
            .Select(n => n!.Value)
            .Distinct()
            .ToList();

        if (seasons.Count == 0)
            return null;

        return seasons.Where(n => n != 0).DefaultIfEmpty(0).Max();
    }

    /// <summary>
    /// Unwatched episodes that come before <paramref name="episodeTvdbId"/>. A
    /// special is never "before" a regular episode and a regular episode is
    /// never "before" a special: see <see cref="SameKind"/>.
    /// </summary>
    public static int CountPriorUnwatched(
        IReadOnlyList<WatchableEpisode> orderedEpisodes,
        IReadOnlySet<int> watchedEpisodeIds,
        int episodeTvdbId)
    {
        var scope = SameKind(orderedEpisodes, episodeTvdbId);
        var index = IndexOf(scope, episodeTvdbId);
        return index <= 0
            ? 0
            : scope.Take(index).Count(e => !watchedEpisodeIds.Contains(e.Id));
    }

    /// <summary>
    /// Episode ids from the first episode through <paramref name="episodeTvdbId"/>
    /// (inclusive), within the same kind (see <see cref="SameKind"/>). If the id
    /// isn't in the list, returns just that id.
    /// </summary>
    public static IReadOnlyList<int> IdsThrough(
        IReadOnlyList<WatchableEpisode> orderedEpisodes,
        int episodeTvdbId)
    {
        var scope = SameKind(orderedEpisodes, episodeTvdbId);
        var index = IndexOf(scope, episodeTvdbId);
        return index < 0
            ? [episodeTvdbId]
            : scope.Take(index + 1).Select(e => e.Id).ToList();
    }

    /// <summary>
    /// The episodes "earlier" is judged among: the specials when the episode is
    /// a special, everything except the specials otherwise. So catching up to
    /// S02E03 never marks a making-of, and marking a special never marks the
    /// whole series.
    /// </summary>
    private static IReadOnlyList<WatchableEpisode> SameKind(
        IReadOnlyList<WatchableEpisode> orderedEpisodes,
        int episodeTvdbId)
    {
        var target = orderedEpisodes.FirstOrDefault(e => e.Id == episodeTvdbId);
        if (target is null)
            return orderedEpisodes;

        return orderedEpisodes.Where(e => e.IsSpecial == target.IsSpecial).ToList();
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
