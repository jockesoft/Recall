using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Web.Services.WatchTracking;

public sealed class WatchTimeService(
    ITheTvDbService theTvDbService,
    IEpisodeWatchRepository episodeWatchRepository,
    ILogger<WatchTimeService> logger) : IWatchTimeService
{
    public async Task<WatchTimeSummary> GetTotalWatchTimeAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var seriesIds = await episodeWatchRepository.GetWatchedSeriesTvdbIdsAsync(userId, cancellationToken);
        if (seriesIds.Count == 0)
            return WatchTimeSummary.Empty;

        var watchedEpisodeIds = await episodeWatchRepository.GetWatchedEpisodeIdsAsync(userId, seriesIds, cancellationToken);
        if (watchedEpisodeIds.Count == 0)
            return WatchTimeSummary.Empty;

        var aggregates = (await Task.WhenAll(
                seriesIds.Select(id => TryGetAggregateAsync(id, cancellationToken))))
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();

        long totalMinutes = 0;
        var counted = 0;
        var seen = new HashSet<int>();

        foreach (var aggregate in aggregates)
        {
            foreach (var episode in aggregate.Episodes)
            {
                if (episode.IsMovie == true) continue;
                if (!watchedEpisodeIds.Contains(episode.Id)) continue;
                if (!seen.Add(episode.Id)) continue;

                counted++;

                var minutes = episode.RuntimeMinutes is > 0
                    ? episode.RuntimeMinutes.Value
                    : aggregate.AverageRuntimeMinutes is > 0 ? aggregate.AverageRuntimeMinutes.Value : 0;

                if (minutes > 0)
                    totalMinutes += minutes;
            }
        }

        return new WatchTimeSummary((int)Math.Min(totalMinutes, int.MaxValue), counted);
    }

    private async Task<SeriesAggregate?> TryGetAggregateAsync(int seriesId, CancellationToken cancellationToken)
    {
        try
        {
            return await theTvDbService.GetSeriesAggregateByIdAsync(seriesId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "WatchTimeService: could not load aggregate for series {SeriesId}.", seriesId);
            return null;
        }
    }
}
