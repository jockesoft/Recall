using Recall.Web.Infrastructure.Persistence.Repositories;

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
    Task<EpisodeWatchResult> MarkEpisodeWatchedAsync(
        Guid userId,
        int seriesTvdbId,
        int episodeTvdbId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="MarkEpisodeWatchedAsync"/> for a one-tap button with no
    /// confirmation (a Dashboard catch-up card): the same checks, but the watch
    /// is written as a batch of one, so the result carries what an "Undo" needs
    /// (<see cref="UndoWatchedBatchAsync"/>). The batch is empty when the
    /// episode was already watched.
    /// </summary>
    Task<UndoableEpisodeWatch> MarkEpisodeWatchedUndoablyAsync(
        Guid userId,
        int seriesTvdbId,
        int episodeTvdbId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Flips an episode's watched state. Un-watching needs no verification (it
    /// only ever removes the user's own row); watching goes through
    /// <see cref="MarkEpisodeWatchedAsync"/>.
    /// </summary>
    Task<EpisodeWatchResult> ToggleEpisodeWatchedAsync(
        Guid userId,
        int seriesTvdbId,
        int episodeTvdbId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks every episode of one season watched, skipping ones already marked
    /// and any whose air date is still in the future.
    /// </summary>
    Task<SeasonWatchResult> MarkSeasonWatchedAsync(
        Guid userId,
        int seriesTvdbId,
        int seasonNumber,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the user's watches for every episode of one season. Returns how many were removed.</summary>
    Task<int> MarkSeasonUnwatchedAsync(
        Guid userId,
        int seriesTvdbId,
        int seasonNumber,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverses one bulk mark (a season, or "this and earlier"), identified by
    /// the <see cref="WatchedBatch.WatchedUtc"/> it returned. Only the rows that
    /// call inserted are removed. Returns how many were removed.
    /// </summary>
    Task<int> UndoWatchedBatchAsync(
        Guid userId,
        int seriesTvdbId,
        DateTime batchWatchedUtc,
        CancellationToken cancellationToken = default);
}

/// <param name="EpisodeFound">False when the episode isn't part of the series' episode list.</param>
/// <param name="MarkedCount">Episodes covered (the target plus earlier aired ones); 0 when nothing was written.</param>
/// <param name="HasAired">False when the target episode's air date is still in the future.</param>
/// <param name="Batch">What was actually inserted, for an undo; null when nothing was written.</param>
/// <param name="CaughtUp">Set when this mark brought the user up to date with the series, or finished it.</param>
public sealed record MarkWatchedThroughResult(
    bool EpisodeFound,
    int MarkedCount,
    bool HasAired = true,
    WatchedBatch? Batch = null,
    SeriesCaughtUp? CaughtUp = null);

/// <param name="SeasonFound">False when the series has no episodes in that season.</param>
/// <param name="Batch">What was actually inserted; <see cref="WatchedBatch.InsertedCount"/> is 0 when the season was already fully marked.</param>
/// <param name="CaughtUp">Set when this mark brought the user up to date with the series, or finished it.</param>
public sealed record SeasonWatchResult(bool SeasonFound, WatchedBatch Batch, SeriesCaughtUp? CaughtUp = null);

/// <param name="Outcome">What happened; a refusal wrote nothing.</param>
/// <param name="Batch">What was inserted, for an undo; null on a refusal.</param>
/// <param name="CaughtUp">Set when this mark brought the user up to date with the series, or finished it.</param>
public sealed record UndoableEpisodeWatch(EpisodeWatchOutcome Outcome, WatchedBatch? Batch = null, SeriesCaughtUp? CaughtUp = null);

/// <summary>What a single mark or toggle did.</summary>
/// <param name="Outcome">What happened; a refusal wrote nothing.</param>
/// <param name="CaughtUp">Set when this mark brought the user up to date with the series, or finished it. Never on an unmark.</param>
public sealed record EpisodeWatchResult(EpisodeWatchOutcome Outcome, SeriesCaughtUp? CaughtUp = null)
{
    public static implicit operator EpisodeWatchResult(EpisodeWatchOutcome outcome) => new(outcome);
}

public enum EpisodeWatchOutcome
{
    MarkedWatched,
    MarkedUnwatched,

    /// <summary>The episode doesn't belong to the series it was submitted with — nothing was written.</summary>
    EpisodeNotInSeries,

    /// <summary>The episode's air date is still in the future — nothing was written.</summary>
    NotAired
}
