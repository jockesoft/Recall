using Recall.Web.Domain.Omdb;

namespace Recall.Web.Infrastructure.Persistence.OmdbCache;

/// <summary>
/// Durable per-series OMDb snapshot store (table <c>cached_series_omdb</c>).
/// Written only by the background <c>UpdateOmdbInfoTimer</c>; reads are for
/// future consumers.
/// </summary>
public interface IOmdbSnapshotStore
{
    /// <summary>The stored OMDb record for a series, or null when absent / not enrichable.</summary>
    Task<OmdbSeries?> GetAsync(int tvdbId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or overwrites the OMDb row for one series. <paramref name="data"/>
    /// may be null — a "we checked, nothing to store" marker that still bumps
    /// <c>retrieved_utc</c> so the series isn't re-checked until it goes stale.
    /// </summary>
    Task UpsertAsync(int tvdbId, string? imdbId, OmdbSeries? data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Distinct TheTVDB ids of cached series whose OMDb snapshot is missing or was
    /// last retrieved before <paramref name="staleBeforeUtc"/> — missing first,
    /// then oldest first, capped at <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<int>> GetSeriesNeedingOmdbAsync(
        DateTime staleBeforeUtc, int limit, CancellationToken cancellationToken = default);
}
