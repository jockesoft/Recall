using Microsoft.Extensions.Options;
using Quartz;
using Recall.Web.Infrastructure.External.Omdb;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Services;
using Recall.Web.Services.External.Omdb;

namespace Recall.Web.Infrastructure.Timers;

/// <summary>
/// Enriches cached movies with OMDb data. Mirrors <see cref="UpdateOmdbInfoTimer"/>
/// exactly, one level down: up to <see cref="MaxRequestsPerRun"/> movies whose OMDb
/// snapshot is missing or older than <see cref="MinRefreshAge"/>, looked up by IMDb
/// id and stored. Shares the same daily <see cref="IOmdbRequestBudget"/> as the
/// series timer and the on-demand episode lookup — the OMDb request cap is global,
/// not per caller.
/// </summary>
[DisallowConcurrentExecution]
public sealed class UpdateMovieOmdbInfoTimer(
    IMovieOmdbSnapshotStore movieOmdbSnapshotStore,
    IOmdbApiClient omdbApiClient,
    IOmdbRequestBudget requestBudget,
    ITheTvDbService theTvDbService,
    IOptions<OmdbOptions> omdbOptions,
    ILogger<UpdateMovieOmdbInfoTimer> logger) : IJob
{
    private static readonly TimeSpan MinRefreshAge = TimeSpan.FromDays(30);
    private const int MaxRequestsPerRun = 30;

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(omdbOptions.Value.ApiKey))
        {
            logger.LogInformation("UpdateMovieOmdbInfoTimer: no OMDb ApiKey configured — skipping run.");
            return;
        }

        var staleBeforeUtc = DateTime.UtcNow - MinRefreshAge;

        var candidates = await movieOmdbSnapshotStore.GetMoviesNeedingOmdbAsync(
            staleBeforeUtc, MaxRequestsPerRun, cancellationToken);

        if (candidates.Count == 0)
        {
            logger.LogInformation("UpdateMovieOmdbInfoTimer: no cached movies are due for an OMDb refresh.");
            return;
        }

        logger.LogInformation(
            "UpdateMovieOmdbInfoTimer: {Count} movies due for OMDb refresh (cap {Cap}).",
            candidates.Count, MaxRequestsPerRun);

        var enriched = 0;
        var skipped = 0;

        foreach (var tvdbId in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var imdbId = await ResolveImdbIdAsync(tvdbId, cancellationToken);

                if (string.IsNullOrWhiteSpace(imdbId))
                {
                    // Nothing to query — record the attempt so we don't re-check for 30 days.
                    await movieOmdbSnapshotStore.UpsertAsync(tvdbId, imdbId: null, data: null, cancellationToken);
                    skipped++;
                    continue;
                }

                if (!requestBudget.TryAcquire())
                {
                    // Shared daily OMDb budget exhausted for today. Stop here rather than
                    // recording false "checked, nothing found" markers for the rest of the
                    // candidates — they stay stale and are retried next run.
                    logger.LogInformation(
                        "UpdateMovieOmdbInfoTimer: daily OMDb request budget exhausted; stopping early ({Enriched} enriched so far).",
                        enriched);
                    break;
                }

                var data = await omdbApiClient.GetByImdbIdAsync(imdbId, "movie", cancellationToken);
                await movieOmdbSnapshotStore.UpsertAsync(tvdbId, imdbId, data, cancellationToken);

                if (data is not null)
                    enriched++;
                else
                    skipped++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad movie shouldn't abort the batch — it stays stale and is retried next run.
                logger.LogWarning(ex, "UpdateMovieOmdbInfoTimer: failed to refresh OMDb data for movie {MovieId}.", tvdbId);
            }
        }

        logger.LogInformation(
            "UpdateMovieOmdbInfoTimer: {Enriched} enriched, {Skipped} recorded without data, of {Total}.",
            enriched, skipped, candidates.Count);
    }

    /// <summary>
    /// Pulls the IMDb id out of the movie's cached TheTVDB aggregate. Layered
    /// cache (Redis → DB), so this is normally not an upstream call.
    /// </summary>
    private async Task<string?> ResolveImdbIdAsync(int tvdbId, CancellationToken cancellationToken)
    {
        var aggregate = await theTvDbService.GetMovieAggregateByIdAsync(tvdbId, cancellationToken);

        return aggregate?.RemoteIds
            .FirstOrDefault(r => string.Equals(r.SourceName, "imdb", StringComparison.OrdinalIgnoreCase)
                                 && !string.IsNullOrWhiteSpace(r.Id))
            ?.Id;
    }
}
