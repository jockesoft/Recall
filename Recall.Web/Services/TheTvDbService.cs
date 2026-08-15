using Recall.Web.Domain.TheTvDb;
using Recall.Web.Mappings;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Web.Services;

public sealed class TheTvDbService(ITheTvDbApiClient apiClient) : ITheTvDbService
{
    public async Task<IReadOnlyList<TvSeriesSummary>> SearchSeriesAsync(string query, CancellationToken cancellationToken = default)
    {
        var items = await apiClient.SearchSeriesAsync(query, cancellationToken);

        return items
            .Where(x => x.Type is null || x.Type.Equals("series", StringComparison.OrdinalIgnoreCase))
            .Select(x => new TvSeriesSummary(
                x.TvdbId,
                x.Name ?? string.Empty,
                x.Overview,
                x.ImageUrl,
                x.Year))
            .ToArray();
    }

    public async Task<TvSeriesDetails?> GetSeriesByIdAsync(int seriesId, CancellationToken cancellationToken = default)
    {
        var aggregate = await apiClient.GetSeriesAggregateByIdAsync(seriesId, "eng", cancellationToken);
        if (aggregate is null) return null;

        return new TvSeriesDetails(
            aggregate.TvdbId,
            aggregate.Name,
            aggregate.Slug,
            aggregate.Overview,
            aggregate.ImageUrl,
            aggregate.FirstAired?.ToString("yyyy-MM-dd"),
            aggregate.Score,
            aggregate.Status != null ? aggregate.Status.Name : "");
    }

    public Task<SeriesAggregate?> GetSeriesAggregateByIdAsync(
        int seriesId,
        CancellationToken cancellationToken = default)
        => apiClient.GetSeriesAggregateByIdAsync(seriesId, "eng", cancellationToken);

    public async Task<Episode?> GetEpisodeDetailsAsync(
        int episodeId,
        CancellationToken cancellationToken = default)
    {
        var episodeDto = await apiClient.GetEpisodeInformationByIdAsync(episodeId, cancellationToken);
        // episodeDto is EpisodeExtendedDto — the more-specific overload in EpisodeMappings maps Score, Awards, ContentRatings.
        return episodeDto?.ToDomain();
    }
    
    public async Task<Series?> GetSeriesByIdExtendedAsync(
        int seriesId,
        CancellationToken cancellationToken = default)
    {
        var seriesDto = await apiClient.GetSeriesByIdExtendedAsync(seriesId, cancellationToken);
        return seriesDto?.ToDomain();
    }
}