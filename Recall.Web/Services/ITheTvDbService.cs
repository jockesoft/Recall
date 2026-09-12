using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;

namespace Recall.Web.Services;

/// <summary>
/// Application service abstraction for TV series operations backed by TheTVDB.
/// </summary>
public interface ITheTvDbService
{
    /// <summary>
    /// Combined series + movie search. Other TheTVDB entity types
    /// (people, companies, ...) are filtered out.
    /// </summary>
    Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<TvSeriesDetails?> GetSeriesByIdAsync(int seriesId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an external id (e.g. an IMDb id) to a TheTVDB series or movie.
    /// A one-shot lookup with no caching tier — used by the watchlist importer,
    /// not the read-heavy paths the three-tier cache exists for.
    /// </summary>
    Task<RemoteIdMatch?> ResolveByRemoteIdAsync(string remoteId, CancellationToken cancellationToken = default);

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

    Task<MovieAggregate?> GetMovieAggregateByIdAsync(int movieId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bypasses the read tiers: fetches the movie aggregate straight from
    /// TheTVDB and overwrites both the local snapshot and the Redis entry.
    /// Returns <c>false</c> when the API has nothing for the id.
    /// </summary>
    Task<bool> RefreshMovieAggregateByIdAsync(int movieId, CancellationToken cancellationToken = default);
}