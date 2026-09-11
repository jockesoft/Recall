using Recall.Web.Domain.Omdb;

namespace Recall.Web.Infrastructure.Persistence.OmdbCache;

/// <summary>
/// Durable per-episode OMDb snapshot store (table <c>cached_episode_omdb</c>).
/// Unlike <see cref="IOmdbSnapshotStore"/>, there's no background job populating
/// this — Episodes/Details reads and writes it directly, fetching from OMDb only
/// when a visitor opens an episode whose snapshot is missing or stale.
/// </summary>
public interface IEpisodeOmdbSnapshotStore
{
    /// <summary>The stored OMDb record for an episode, or null when absent / not enrichable.</summary>
    Task<OmdbSeries?> GetAsync(int episodeTvdbId, CancellationToken cancellationToken = default);

    /// <summary>When the episode's OMDb snapshot was last retrieved, or null when there's no row yet.</summary>
    Task<DateTime?> GetRetrievedUtcAsync(int episodeTvdbId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or overwrites the OMDb row for one episode. <paramref name="data"/>
    /// may be null — a "we checked, nothing to store" marker that still bumps
    /// <c>retrieved_utc</c> so the episode isn't re-checked until it goes stale.
    /// </summary>
    Task UpsertAsync(int episodeTvdbId, string? imdbId, OmdbSeries? data, CancellationToken cancellationToken = default);
}
