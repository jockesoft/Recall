using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Episodes;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Filter;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Movies;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Search;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;

namespace Recall.Web.Services.External.TheTvDb;

/// <summary>
/// Low-level transport client for TheTVDB API.
/// </summary>
public interface ITheTvDbApiClient
{
    /// <summary>
    /// Unscoped TheTVDB search — returns every entity type TheTVDB matches
    /// (series, movies, people, companies, ...). Filtering to the content
    /// types Recall cares about happens in <see cref="Mappings.SearchResultMappings"/>.
    /// </summary>
    Task<IReadOnlyList<SearchResultDto>> SearchAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an external id (e.g. an IMDb id like "tt2479478") to whatever
    /// TheTVDB entities carry it — used by the watchlist importer, which only
    /// has IMDb ids to start from.
    /// </summary>
    Task<IReadOnlyList<SearchByRemoteIdResultDto>> SearchByRemoteIdAsync(
        string remoteId, CancellationToken cancellationToken = default);

    Task<SeriesAggregate?> GetSeriesAggregateByIdAsync(
        int seriesId,
        string language = "eng",
        CancellationToken cancellationToken = default);

    Task<SeriesTranslationDataDto?> GetSeriesTranslationByLanguageAsync(
        int seriesId,
        string language,
        CancellationToken cancellationToken = default);
    
    Task<SeriesDataDto?> GetSeriesByIdExtendedAsync(int seriesId, CancellationToken cancellationToken = default);
    Task<EpisodeExtendedDto?> GetEpisodeInformationByIdAsync(
        int episodeId,
        CancellationToken cancellationToken = default);

    Task<MovieAggregate?> GetMovieAggregateByIdAsync(
        int movieId,
        string language = "eng",
        CancellationToken cancellationToken = default);

    Task<MovieDataDto?> GetMovieByIdExtendedAsync(int movieId, CancellationToken cancellationToken = default);

    Task<SeriesTranslationDataDto?> GetMovieTranslationByLanguageAsync(
        int movieId,
        string language,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The first page (500 base records) of TheTVDB's series filter: one
    /// request. For the Discover lists, fetched by the daily job only.
    /// </summary>
    Task<IReadOnlyList<FilterRecordDto>> FilterSeriesAsync(TitleFilter filter, CancellationToken cancellationToken = default);

    /// <summary>The first page (500 base records) of TheTVDB's movie filter: one request.</summary>
    Task<IReadOnlyList<FilterRecordDto>> FilterMoviesAsync(TitleFilter filter, CancellationToken cancellationToken = default);
}
