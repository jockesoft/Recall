using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Episodes;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Search;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;

namespace Recall.Web.Services.External.TheTvDb;

/// <summary>
/// Low-level transport client for TheTVDB API.
/// </summary>
public interface ITheTvDbApiClient
{
    Task<IReadOnlyList<SearchResultDto>> SearchSeriesAsync(string query, CancellationToken cancellationToken = default);

    Task<SeriesAggregate?> GetSeriesAggregateByIdAsync(
        int seriesId,
        string language = "eng",
        CancellationToken cancellationToken = default);

    Task<SeriesTranslationDataDto?> GetSeriesTranslationByLanguageAsync(
        int seriesId,
        string language,
        CancellationToken cancellationToken = default);
    
    Task<SeriesDataDto?> GetSeriesByIdExtendedAsync(int seriesId, CancellationToken cancellationToken = default);
    Task<EpisodeTranslationDataDto?> GetEpisodeTranslationByLanguageAsync(
        int episodeId,
        string language,
        CancellationToken cancellationToken = default);
}