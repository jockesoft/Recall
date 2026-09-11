using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Infrastructure.Persistence.TvdbCache;

/// <summary>
/// Durable local store for TheTVDB domain snapshots — the middle tier between
/// Redis and the API. Reads fall through cache → DB → API; first-time writes are
/// insert-if-absent. The background refresh job additionally overwrites existing
/// aggregate rows via <see cref="UpsertSeriesAggregateAsync"/>.
/// </summary>
public interface ITvdbSnapshotStore
{
    Task<SeriesAggregate?> GetSeriesAggregateAsync(int tvdbId, string language, CancellationToken cancellationToken = default);
    Task SaveSeriesAggregateAsync(SeriesAggregate aggregate, string language, CancellationToken cancellationToken = default);

    Task<Series?> GetSeriesExtendedAsync(int tvdbId, CancellationToken cancellationToken = default);
    Task SaveSeriesExtendedAsync(Series series, CancellationToken cancellationToken = default);

    Task<MovieAggregate?> GetMovieAggregateAsync(int tvdbId, string language, CancellationToken cancellationToken = default);
    Task SaveMovieAggregateAsync(MovieAggregate aggregate, string language, CancellationToken cancellationToken = default);

    Task<Episode?> GetEpisodeExtendedAsync(int episodeTvdbId, CancellationToken cancellationToken = default);

    /// <summary>Batched lookup of cached episode snapshots by id. Missing/corrupt ids are simply absent from the result.</summary>
    Task<IReadOnlyDictionary<int, Episode>> GetEpisodesExtendedAsync(
        IReadOnlyCollection<int> episodeTvdbIds, CancellationToken cancellationToken = default);

    Task SaveEpisodeExtendedAsync(Episode episode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cached aggregate rows flagged <c>keep_updated</c> whose snapshot was last
    /// retrieved before <paramref name="staleBeforeUtc"/>, oldest first and
    /// capped at <paramref name="limit"/>. Feeds the background refresh job.
    /// </summary>
    Task<IReadOnlyList<CachedAggregateKey>> GetAggregatesNeedingRefreshAsync(
        DateTime staleBeforeUtc, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cached movie aggregate rows flagged <c>keep_updated</c> whose snapshot was
    /// last retrieved before <paramref name="staleBeforeUtc"/>, oldest first and
    /// capped at <paramref name="limit"/>. Feeds the background refresh job.
    /// </summary>
    Task<IReadOnlyList<CachedAggregateKey>> GetMovieAggregatesNeedingRefreshAsync(
        DateTime staleBeforeUtc, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cached episode snapshots that are due a refresh: last retrieved before
    /// <paramref name="staleBeforeUtc"/>; still titled "TBA" and last retrieved
    /// before <paramref name="tbaStaleBeforeUtc"/>; or missing its still image
    /// despite having already aired (denormalized air date on or before
    /// <paramref name="today"/>), last retrieved before
    /// <paramref name="imageChaseBeforeUtc"/> and with fewer than
    /// <paramref name="maxImageChaseAttempts"/> consecutive imageless refreshes.
    /// Oldest first, capped at <paramref name="limit"/>. Feeds the background
    /// refresh job so episode data can't drift from the series aggregate.
    /// </summary>
    Task<IReadOnlyList<int>> GetEpisodesNeedingRefreshAsync(
        DateTime staleBeforeUtc,
        DateTime tbaStaleBeforeUtc,
        DateTime imageChaseBeforeUtc,
        DateOnly today,
        int maxImageChaseAttempts,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts the aggregate snapshot for one series + language, or overwrites it
    /// (payload, denormalized columns and <c>retrieved_utc</c>) if a row exists.
    /// </summary>
    Task UpsertSeriesAggregateAsync(SeriesAggregate aggregate, string language, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts the aggregate snapshot for one movie + language, or overwrites it
    /// (payload, denormalized columns and <c>retrieved_utc</c>) if a row exists.
    /// </summary>
    Task UpsertMovieAggregateAsync(MovieAggregate aggregate, string language, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts the extended-episode snapshot, or overwrites it (payload,
    /// denormalized columns and <c>retrieved_utc</c>) if a row exists.
    /// </summary>
    Task UpsertEpisodeExtendedAsync(Episode episode, CancellationToken cancellationToken = default);

    /// <summary>
    /// For each episode in <paramref name="aggregate"/> that has an image, patches
    /// any matching <c>cached_episode_extended</c> row currently missing one —
    /// purely local reconciliation using data already fetched for the aggregate
    /// refresh, no extra TheTVDB call. Leaves <c>retrieved_utc</c> untouched.
    /// Returns the patched episodes (post-patch) so callers can refresh other
    /// caches, e.g. Redis, for them.
    /// </summary>
    Task<IReadOnlyList<Episode>> BackfillEpisodeImagesFromAggregateAsync(
        SeriesAggregate aggregate, CancellationToken cancellationToken = default);
}

/// <summary>Composite key of a <c>cached_series_aggregate</c> or <c>cached_movie_aggregate</c> row.</summary>
public readonly record struct CachedAggregateKey(int TvdbId, string Language);
