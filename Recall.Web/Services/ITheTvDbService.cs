using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;

namespace Recall.Web.Services;

/// <summary>
/// Application service abstraction for TV series operations backed by TheTVDB.
/// </summary>
public interface ITheTvDbService
{
    Task<IReadOnlyList<TvSeriesSummary>> SearchSeriesAsync(string query, CancellationToken cancellationToken = default);
    Task<TvSeriesDetails?> GetSeriesByIdAsync(int seriesId, CancellationToken cancellationToken = default);

    Task<SeriesAggregate?> GetSeriesAggregateByIdAsync(int seriesId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bypasses the read tiers: fetches the series aggregate straight from
    /// TheTVDB and overwrites both the local snapshot and the Redis entry.
    /// Returns <c>false</c> when the API has nothing for the id.
    /// </summary>
    Task<bool> RefreshSeriesAggregateByIdAsync(int seriesId, CancellationToken cancellationToken = default);

    Task<Episode?> GetEpisodeDetailsAsync(int episodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bypasses the read tiers: fetches the extended episode straight from
    /// TheTVDB and overwrites both the local snapshot and the Redis entry.
    /// Returns <c>false</c> when the API has nothing for the id.
    /// </summary>
    Task<bool> RefreshEpisodeDetailsByIdAsync(int episodeId, CancellationToken cancellationToken = default);

    Task<Series?> GetSeriesByIdExtendedAsync(int seriesId, CancellationToken cancellationToken = default);
}