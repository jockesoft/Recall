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
        var (episodes, watched) = await LoadEpisodesAndWatchedAsync(userId, seriesTvdbId, cancellationToken);

        return WatchProgressCalculator.Build(seriesTvdbId, episodes, watched, Today);
    }

    /// <summary>
    /// Loads a series' ordered episodes (TheTVDB, via the layered cache) and the
    /// user's watched ids (the DB) concurrently — the two calls are independent
    /// and use separate DbContext instances, so there's no benefit to awaiting
    /// them one after another.
    /// </summary>
    private async Task<(IReadOnlyList<WatchableEpisode> Ordered, IReadOnlySet<int> Watched)> LoadEpisodesAndWatchedAsync(
        Guid userId,
        int seriesTvdbId,
        CancellationToken cancellationToken)
    {
        var orderedTask = GetOrderedEpisodesAsync(seriesTvdbId, cancellationToken);
        var watchedTask = episodeWatchRepository.GetWatchedEpisodeIdsAsync(userId, seriesTvdbId, cancellationToken);

        await Task.WhenAll(orderedTask, watchedTask);

        return (orderedTask.Result, watchedTask.Result);
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
            var (ordered, watched) = await LoadEpisodesAndWatchedAsync(userId, seriesTvdbId, cancellationToken);

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

        if (!ordered.Any(e => e.Id == episodeTvdbId))
        {
            // The episode isn't part of this series' known episode list (a stale
            // cache, a renumbered/removed episode, or route/form values that
            // don't actually match) — don't record a watch against the wrong
            // series just because IdsThrough would otherwise fall back to it.
            return new MarkWatchedThroughResult(EpisodeFound: false, MarkedCount: 0);
        }

        var idsToMark = WatchProgressCalculator.IdsThrough(ordered, episodeTvdbId);
        await episodeWatchRepository.MarkWatchedRangeAsync(userId, seriesTvdbId, idsToMark, cancellationToken);

        return new MarkWatchedThroughResult(EpisodeFound: true, idsToMark.Count);
    }
}
