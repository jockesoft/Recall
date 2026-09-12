using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Episodes;
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
    Task<EpisodeTranslationDataDto?> GetEpisodeTranslationByLanguageAsync(
        int episodeId,
        string language,
        CancellationToken cancellationToken = default);

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
}