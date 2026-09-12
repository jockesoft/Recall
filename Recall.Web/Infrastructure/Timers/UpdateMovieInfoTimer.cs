using Quartz;
using Recall.Web.Infrastructure.Persistence.TvdbCache;
using Recall.Web.Services;

namespace Recall.Web.Infrastructure.Timers;

/// <summary>
/// Keeps the local TheTVDB movie snapshots fresh. Each run refreshes up to
/// <see cref="MaxMoviesPerRun"/> <c>cached_movie_aggregate</c> rows that carry
/// TheTVDB's <c>keep_updated</c> flag and are older than <see cref="MinRefreshAge"/>.
/// Mirrors the series-aggregate half of <see cref="UpdateTvDbInfoTimer"/> — movies
/// have no seasons/episodes, so there's no equivalent episode-refresh pass.
/// </summary>
[DisallowConcurrentExecution]
public class UpdateMovieInfoTimer(
    ITvdbSnapshotStore snapshotStore,
    ITheTvDbService theTvDbService,
    ILogger<UpdateMovieInfoTimer> logger) : IJob
{
    private static readonly TimeSpan MinRefreshAge = TimeSpan.FromHours(12);
    private const int MaxMoviesPerRun = 10;

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var staleBeforeUtc = DateTime.UtcNow - MinRefreshAge;

        var candidates = await snapshotStore.GetMovieAggregatesNeedingRefreshAsync(
            staleBeforeUtc, MaxMoviesPerRun, cancellationToken);

        if (candidates.Count == 0)
        {
            logger.LogInformation("UpdateMovieInfoTimer: no keep-updated movies are due for a refresh.");
            return;
        }

        logger.LogInformation(
            "UpdateMovieInfoTimer: refreshing {Count} movie(s) not updated since {StaleBefore:u} (cap {Cap}).",
            candidates.Count, staleBeforeUtc, MaxMoviesPerRun);

        var refreshed = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await theTvDbService.RefreshMovieAggregateByIdAsync(candidate.TvdbId, cancellationToken))
                    refreshed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "UpdateMovieInfoTimer: failed to refresh movie {MovieId}.", candidate.TvdbId);
            }
        }

        logger.LogInformation("UpdateMovieInfoTimer: refreshed {Refreshed}/{Total} movies.", refreshed, candidates.Count);
    }
}
