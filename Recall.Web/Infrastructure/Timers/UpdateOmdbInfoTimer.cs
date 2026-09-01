//-----------------------------------------------------------------------
// <copyright file="UpdateOmdbInfoTimer.cs" company="Kevant Development">
//     Copyright (c) Kevant Development. All rights reserved.
// </copyright>
// <author>Joakim Fredlund</author>
//-----------------------------------------------------------------------

using Microsoft.Extensions.Options;
using Quartz;
using Recall.Web.Infrastructure.External.Omdb;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Services;
using Recall.Web.Services.External.Omdb;

namespace Recall.Web.Infrastructure.Timers;

/// <summary>
/// Enriches cached series with OMDb data. Scheduled hourly in <c>Program.cs</c>.
/// Each run takes up to <see cref="MaxRequestsPerRun"/> series whose OMDb
/// snapshot is missing or older than <see cref="MinRefreshAge"/>, looks each one
/// up by its IMDb id, and stores the result. A series with no IMDb id (or that
/// OMDb can't resolve) still gets a dated marker row so it isn't retried until it
/// goes stale again.
///
/// OMDb allows 1000 requests/day; the hourly cap keeps us to at most
/// 30 × 24 = 720 even in the worst case.
/// </summary>
[DisallowConcurrentExecution]
public sealed class UpdateOmdbInfoTimer(
    IOmdbSnapshotStore omdbSnapshotStore,
    IOmdbApiClient omdbApiClient,
    ITheTvDbService theTvDbService,
    IOptions<OmdbOptions> omdbOptions,
    ILogger<UpdateOmdbInfoTimer> logger) : IJob
{
    /// <summary>Only refresh a series' OMDb data this rarely.</summary>
    private static readonly TimeSpan MinRefreshAge = TimeSpan.FromDays(30);

    /// <summary>Upper bound on OMDb calls per run (job runs hourly).</summary>
    private const int MaxRequestsPerRun = 30;

    public async Task Execute(IJobExecutionContext context)
    {
        var cancellationToken = context.CancellationToken;

        if (string.IsNullOrWhiteSpace(omdbOptions.Value.ApiKey))
        {
            logger.LogInformation("UpdateOmdbInfoTimer: no OMDb ApiKey configured — skipping run.");
            return;
        }

        var staleBeforeUtc = DateTime.UtcNow - MinRefreshAge;

        var candidates = await omdbSnapshotStore.GetSeriesNeedingOmdbAsync(
            staleBeforeUtc, MaxRequestsPerRun, cancellationToken);

        if (candidates.Count == 0)
        {
            logger.LogInformation("UpdateOmdbInfoTimer: no cached series are due for an OMDb refresh.");
            return;
        }

        logger.LogInformation(
            "UpdateOmdbInfoTimer: {Count} series due for OMDb refresh (cap {Cap}).",
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
                    await omdbSnapshotStore.UpsertAsync(tvdbId, imdbId: null, data: null, cancellationToken);
                    skipped++;
                    continue;
                }

                var data = await omdbApiClient.GetByImdbIdAsync(imdbId, cancellationToken);
                await omdbSnapshotStore.UpsertAsync(tvdbId, imdbId, data, cancellationToken);

                if (data is not null)
                    enriched++;
                else
                    skipped++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad series shouldn't abort the batch — it stays stale and is retried next run.
                logger.LogWarning(ex, "UpdateOmdbInfoTimer: failed to refresh OMDb data for series {SeriesId}.", tvdbId);
            }
        }

        logger.LogInformation(
            "UpdateOmdbInfoTimer: {Enriched} enriched, {Skipped} recorded without data, of {Total}.",
            enriched, skipped, candidates.Count);
    }

    /// <summary>
    /// Pulls the IMDb id out of the series' cached TheTVDB aggregate. Layered
    /// cache (Redis → DB), so this is normally not an upstream call.
    /// </summary>
    private async Task<string?> ResolveImdbIdAsync(int tvdbId, CancellationToken cancellationToken)
    {
        var aggregate = await theTvDbService.GetSeriesAggregateByIdAsync(tvdbId, cancellationToken);

        return aggregate?.RemoteIds
            .FirstOrDefault(r => string.Equals(r.SourceName, "imdb", StringComparison.OrdinalIgnoreCase)
                                 && !string.IsNullOrWhiteSpace(r.Id))
            ?.Id;
    }
}
