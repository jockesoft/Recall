using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Web.Services.WatchTracking;

public sealed class WatchProgressService(
    ITheTvDbService theTvDbService,
    IEpisodeWatchRepository episodeWatchRepository,
    ILogger<WatchProgressService> logger)
    : IWatchProgressService
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    public SeriesWatchProgress BuildProgress(
        int seriesTvdbId,
        IEnumerable<WatchableEpisode> episodes,
        IReadOnlySet<int> watchedEpisodeIds)
        => WatchProgressCalculator.Build(seriesTvdbId, episodes, watchedEpisodeIds, Today);

    public async Task<SeriesWatchProgress> GetSeriesProgressAsync(
        Guid userId,
        int seriesTvdbId,
        CancellationToken cancellationToken = default)
    {
        var episodes = await GetOrderedEpisodesAsync(seriesTvdbId, cancellationToken);
        var watched = await episodeWatchRepository.GetWatchedEpisodeIdsAsync(userId, seriesTvdbId, cancellationToken);

        return WatchProgressCalculator.Build(seriesTvdbId, episodes, watched, Today);
    }

    public async Task<IReadOnlyList<WatchableEpisode>> GetOrderedEpisodesAsync(
        int seriesTvdbId,
        CancellationToken cancellationToken = default)
    {
        var series = await theTvDbService.GetSeriesByIdExtendedAsync(seriesTvdbId, cancellationToken);

        return series is null
            ? []
            : WatchProgressCalculator.Order(series.ToWatchableEpisodes());
    }

    public async Task<int> GetPriorUnwatchedCountAsync(
        Guid userId,
        int seriesTvdbId,
        int episodeTvdbId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ordered = await GetOrderedEpisodesAsync(seriesTvdbId, cancellationToken);
            var watched = await episodeWatchRepository.GetWatchedEpisodeIdsAsync(userId, seriesTvdbId, cancellationToken);

            return WatchProgressCalculator.CountPriorUnwatched(ordered, watched, episodeTvdbId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nice-to-have prompt for a "catch up?" modal — never worth a broken page.
            logger.LogWarning(ex, "Could not compute prior-unwatched count for series {SeriesId}, episode {EpisodeId}.", seriesTvdbId, episodeTvdbId);
            return 0;
        }
    }

    public async Task<MarkWatchedThroughResult> MarkWatchedThroughAsync(
        Guid userId,
        int seriesTvdbId,
        int episodeTvdbId,
        CancellationToken cancellationToken = default)
    {
        var ordered = await GetOrderedEpisodesAsync(seriesTvdbId, cancellationToken);

        var episodeFound = ordered.Any(e => e.Id == episodeTvdbId);
        var idsToMark = WatchProgressCalculator.IdsThrough(ordered, episodeTvdbId);

        await episodeWatchRepository.MarkWatchedRangeAsync(userId, seriesTvdbId, idsToMark, cancellationToken);

        return new MarkWatchedThroughResult(episodeFound, idsToMark.Count);
    }
}
