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
    Task<Episode?> GetEpisodeDetailsAsync(int episodeId, CancellationToken cancellationToken = default);
}