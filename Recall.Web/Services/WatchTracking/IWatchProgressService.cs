namespace Recall.Web.Services.WatchTracking;

/// <summary>
/// Owns all "where is the user in this series" reasoning: the next episode to
/// watch, up-to-date state, prior-unwatched counts, and bulk "mark through".
/// Page models call this instead of duplicating the logic.
/// </summary>
public interface IWatchProgressService
{
    /// <summary>
    /// Builds watch progress from episodes the caller already holds (e.g. a
    /// series aggregate already loaded for the page). Does not call TheTVDB.
    /// </summary>
    SeriesWatchProgress BuildProgress(
        int seriesTvdbId,
        IEnumerable<WatchableEpisode> episodes,
        IReadOnlySet<int> watchedEpisodeIds);

    /// <summary>Fetches the series' episodes and the user's watched ids, then builds progress.</summary>
    Task<SeriesWatchProgress> GetSeriesProgressAsync(
        Guid userId,
        int seriesTvdbId,
        CancellationToken cancellationToken = default);

    /// <summary>Ordered, non-movie episode list for a series (season/episode order).</summary>
    Task<IReadOnlyList<WatchableEpisode>> GetOrderedEpisodesAsync(
        int seriesTvdbId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How many episodes before the given one (in season/episode order) the user
    /// has not marked watched. Fails closed (returns 0) on any error.
    /// </summary>
    Task<int> GetPriorUnwatchedCountAsync(
        Guid userId,
        int seriesTvdbId,
        int episodeTvdbId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the given episode and every earlier episode (season/episode order)
    /// watched, skipping ones already marked.
    /// </summary>
    Task<MarkWatchedThroughResult> MarkWatchedThroughAsync(
        Guid userId,
        int seriesTvdbId,
        int episodeTvdbId,
        CancellationToken cancellationToken = default);
}

public sealed record MarkWatchedThroughResult(bool EpisodeFound, int MarkedCount);
