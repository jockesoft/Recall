using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;
using Recall.Web.Services.External.TheTvDb;
using Recall.Web.Services.Models;

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
            aggregate.Score);
    }

    public Task<SeriesAggregate?> GetSeriesAggregateByIdAsync(
        int seriesId,
        CancellationToken cancellationToken = default)
        => apiClient.GetSeriesAggregateByIdAsync(seriesId, "eng", cancellationToken);
    
    public Task<EpisodeDto?> GetEpisodeDetailsAsync(
        int episodeId,
        CancellationToken cancellationToken = default)
        => apiClient.GetEpisodeInformationByIdAsync(episodeId, cancellationToken);
}