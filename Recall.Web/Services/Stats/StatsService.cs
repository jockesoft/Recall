using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Services.Stats;

public interface IStatsService
{
    /// <summary>
    /// The user's stats: what the Stats page shows, and the watch-time total on
    /// Profile. Reads the database and the metadata caches only; it never calls
    /// TheTVDB or OMDb, so a title that is not cached is counted without a
    /// length (see <see cref="StatsGaps"/>).
    /// </summary>
    Task<UserStats> GetAsync(Guid userId, CancellationToken cancellationToken = default);
}

public sealed class StatsService(
    IEpisodeWatchRepository episodeWatchRepository,
    IMovieWatchRepository movieWatchRepository,
    ITrackedSeriesRepository trackedSeriesRepository,
    IRatingRepository ratingRepository,
    ITheTvDbService theTvDbService,
    TimeProvider timeProvider,
    ILogger<StatsService> logger) : IStatsService
{
    // How many cache reads run at once. A read that misses Redis opens its own
    // database connection, so a user with hundreds of watched movies must not
    // start hundreds of them together.
    private const int MaxConcurrentCacheReads = 16;

    public async Task<UserStats> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Four queries, one after another: they share the request's DbContext.
        var episodeWatches = await episodeWatchRepository.GetWatchesAsync(userId, cancellationToken);
        var movieWatches = await movieWatchRepository.GetWatchedMoviesAsync(userId, cancellationToken);
        var ratingCounts = await ratingRepository.GetValueCountsAsync(userId, cancellationToken);

        if (episodeWatches.Count == 0 && movieWatches.Count == 0 && ratingCounts.Count == 0)
            return UserStats.Empty;

        var tracked = await trackedSeriesRepository.GetByUserAsync(userId, cancellationToken);
        var trackedIds = tracked.Select(t => t.TvdbId).ToHashSet();

        // Only series something was watched of: a tracked series with no
        // watches cannot be "finished" and adds nothing else.
        var seriesIds = episodeWatches.Select(w => w.SeriesTvdbId).Distinct().ToList();
        var movieIds = movieWatches.Select(w => w.MovieTvdbId).Distinct().ToList();

        // The cache reads use their own contexts (IDbContextFactory), so they
        // may run side by side.
        using var gate = new SemaphoreSlim(MaxConcurrentCacheReads);
        var seriesTask = ReadAllAsync(seriesIds, gate, theTvDbService.GetCachedSeriesAggregateAsync, "series", cancellationToken);
        var moviesTask = ReadAllAsync(movieIds, gate, theTvDbService.GetCachedMovieAggregateAsync, "movie", cancellationToken);
        await Task.WhenAll(seriesTask, moviesTask);

        var now = AirDate.Now(timeProvider);
        var today = DateOnly.FromDateTime(now);

        return StatsBuilder.Build(
            new StatsInput(episodeWatches, movieWatches, seriesTask.Result, moviesTask.Result, trackedIds, ratingCounts),
            now,
            StatsWindow.LastTwelveMonths(today));
    }

    private async Task<IReadOnlyDictionary<int, T>> ReadAllAsync<T>(
        IReadOnlyList<int> ids,
        SemaphoreSlim gate,
        Func<int, CancellationToken, Task<T?>> read,
        string kind,
        CancellationToken cancellationToken)
        where T : class
    {
        var found = await Task.WhenAll(ids.Select(async id =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return (Id: id, Value: await read(id, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreadable title must not take the page down; it is
                // counted as not cached.
                logger.LogWarning(ex, "Stats: could not read cached {Kind} {TvdbId}.", kind, id);
                return (Id: id, Value: (T?)null);
            }
            finally
            {
                gate.Release();
            }
        }));

        return found
            .Where(f => f.Value is not null)
            .ToDictionary(f => f.Id, f => f.Value!);
    }
}
