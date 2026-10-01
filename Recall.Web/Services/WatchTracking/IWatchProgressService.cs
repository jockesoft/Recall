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

    /// <summary>
    /// Ordered, non-movie episode list for a series (season/episode order), read
    /// from the series aggregate — the same source the pages render from.
    /// </summary>
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
    /// watched, skipping ones already marked and any whose air date is still in
    /// the future. Writes nothing when the episode isn't part of the series or
    /// hasn't aired itself.
    /// </summary>
    Task<MarkWatchedThroughResult> MarkWatchedThroughAsync(
        Guid userId,
        int seriesTvdbId,
        int episodeTvdbId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks one episode watched — the only path a page should use to record a
    /// watch, because it verifies the pair first: the episode must belong to
    /// <paramref name="seriesTvdbId"/> and must not have a future air date.
    /// Never returns <see cref="EpisodeWatchOutcome.MarkedUnwatched"/>.
    /// </summary>
    Task<EpisodeWatchOutcome> MarkEpisodeWatchedAsync(
        Guid userId,
        int seriesTvdbId,
        int episodeTvdbId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Flips an episode's watched state. Un-watching needs no verification (it
    /// only ever removes the user's own row); watching goes through
    /// <see cref="MarkEpisodeWatchedAsync"/>.
    /// </summary>
    Task<EpisodeWatchOutcome> ToggleEpisodeWatchedAsync(
        Guid userId,
        int seriesTvdbId,
        int episodeTvdbId,
        CancellationToken cancellationToken = default);
}

/// <param name="EpisodeFound">False when the episode isn't part of the series' episode list.</param>
/// <param name="MarkedCount">Episodes covered (the target plus earlier aired ones); 0 when nothing was written.</param>
/// <param name="HasAired">False when the target episode's air date is still in the future.</param>
public sealed record MarkWatchedThroughResult(bool EpisodeFound, int MarkedCount, bool HasAired = true);

public enum EpisodeWatchOutcome
{
    MarkedWatched,
    MarkedUnwatched,

    /// <summary>The episode doesn't belong to the series it was submitted with — nothing was written.</summary>
    EpisodeNotInSeries,

    /// <summary>The episode's air date is still in the future — nothing was written.</summary>
    NotAired
}
