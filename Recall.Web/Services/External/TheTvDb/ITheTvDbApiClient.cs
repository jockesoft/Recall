using Recall.Web.Infrastructure.External.TheTvDb.Dto.Search;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;

namespace Recall.Web.Services.External.TheTvDb;

/// <summary>
/// Low-level transport client for TheTVDB API.
/// </summary>
public interface ITheTvDbApiClient
{
    Task<IReadOnlyList<SearchResultDto>> SearchSeriesAsync(string query, CancellationToken cancellationToken = default);
    Task<SeriesDataDto?> GetSeriesByIdAsync(int seriesId, CancellationToken cancellationToken = default);
}