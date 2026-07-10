using Recall.Web.Services.External.TheTvDb;
using Recall.Web.Services.Models;

namespace Recall.Web.Services;

public sealed class TheTvDbService : ITheTvDbService
{
    private readonly ITheTvDbApiClient _apiClient;

    public TheTvDbService(ITheTvDbApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public async Task<IReadOnlyList<TvSeriesSummary>> SearchSeriesAsync(string query, CancellationToken cancellationToken = default)
    {
        var items = await _apiClient.SearchSeriesAsync(query, cancellationToken);

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
        var data = await _apiClient.GetSeriesByIdAsync(seriesId, cancellationToken);
        if (data is null) return null;

        return new TvSeriesDetails(
            data.Id,
            data.Name ?? string.Empty,
            data.Slug,
            data.Overview,
            data.Image,
            data.FirstAired,
            data.Score);
    }
}