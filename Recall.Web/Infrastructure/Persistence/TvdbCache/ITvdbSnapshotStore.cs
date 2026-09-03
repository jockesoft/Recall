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

    Task<Episode?> GetEpisodeExtendedAsync(int episodeTvdbId, CancellationToken cancellationToken = default);
    Task SaveEpisodeExtendedAsync(Episode episode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cached aggregate rows flagged <c>keep_updated</c> whose snapshot was last
    /// retrieved before <paramref name="staleBeforeUtc"/>, oldest first and
    /// capped at <paramref name="limit"/>. Feeds the background refresh job.
    /// </summary>
    Task<IReadOnlyList<CachedAggregateKey>> GetAggregatesNeedingRefreshAsync(
        DateTime staleBeforeUtc, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cached episode snapshots that are due a refresh: either last retrieved
    /// before <paramref name="staleBeforeUtc"/>, or still titled "TBA" and last
    /// retrieved before <paramref name="tbaStaleBeforeUtc"/>. Oldest first,
    /// capped at <paramref name="limit"/>. Feeds the background refresh job so
    /// episode data can't drift from the series aggregate.
    /// </summary>
    Task<IReadOnlyList<int>> GetEpisodesNeedingRefreshAsync(
        DateTime staleBeforeUtc, DateTime tbaStaleBeforeUtc, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts the aggregate snapshot for one series + language, or overwrites it
    /// (payload, denormalized columns and <c>retrieved_utc</c>) if a row exists.
    /// </summary>
    Task UpsertSeriesAggregateAsync(SeriesAggregate aggregate, string language, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts the extended-episode snapshot, or overwrites it (payload,
    /// denormalized columns and <c>retrieved_utc</c>) if a row exists.
    /// </summary>
    Task UpsertEpisodeExtendedAsync(Episode episode, CancellationToken cancellationToken = default);
}

/// <summary>Composite key of a <c>cached_series_aggregate</c> row.</summary>
public readonly record struct CachedAggregateKey(int TvdbId, string Language);
