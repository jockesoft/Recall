using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Infrastructure.Persistence.TvdbCache;

/// <summary>
/// Durable local store for TheTVDB domain snapshots — the middle tier between
/// Redis and the API. Writes are insert-if-absent (no overwrite); a refresh
/// path is out of scope for now.
/// </summary>
public interface ITvdbSnapshotStore
{
    Task<SeriesAggregate?> GetSeriesAggregateAsync(int tvdbId, string language, CancellationToken cancellationToken = default);
    Task SaveSeriesAggregateAsync(SeriesAggregate aggregate, string language, CancellationToken cancellationToken = default);

    Task<Series?> GetSeriesExtendedAsync(int tvdbId, CancellationToken cancellationToken = default);
    Task SaveSeriesExtendedAsync(Series series, CancellationToken cancellationToken = default);

    Task<Episode?> GetEpisodeExtendedAsync(int episodeTvdbId, CancellationToken cancellationToken = default);
    Task SaveEpisodeExtendedAsync(Episode episode, CancellationToken cancellationToken = default);
}
