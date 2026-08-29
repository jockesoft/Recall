//-----------------------------------------------------------------------
// <copyright file="UpdateTvDbInfoTimer.cs" company="Kevant Development">
//     Copyright (c) Kevant Development. All rights reserved.
// </copyright>
// <author>Joakim Fredlund</author>
//-----------------------------------------------------------------------

using Quartz;
using Recall.Web.Infrastructure.Persistence.TvdbCache;
using Recall.Web.Services;

namespace Recall.Web.Infrastructure.Timers;

/// <summary>
/// Keeps the local <c>cached_series_aggregate</c> rows fresh. Each run picks up
/// to <see cref="MaxSeriesPerRun"/> series that carry TheTVDB's
/// <c>keep_updated</c> flag and whose snapshot is older than
/// <see cref="MinRefreshAge"/>, then re-fetches each from TheTVDB. The age check
/// means the job can safely be scheduled far more often than the refresh cadence
/// without hammering the upstream API.
/// </summary>
[DisallowConcurrentExecution]
public class UpdateTvDbInfoTimer(
    ITvdbSnapshotStore snapshotStore,
    ITheTvDbService theTvDbService,
    ILogger<UpdateTvDbInfoTimer> logger) : IJob
{
    /// <summary>Don't re-fetch a series from TheTVDB more often than this.</summary>
    private static readonly TimeSpan MinRefreshAge = TimeSpan.FromHours(12);

    /// <summary>Upper bound on TheTVDB calls per run — deliberately low to start.</summary>
    private const int MaxSeriesPerRun = 10;

    public async Task Execute(IJobExecutionContext context)
    {
        var cancellationToken = context.CancellationToken;
        var staleBeforeUtc = DateTime.UtcNow - MinRefreshAge;

        var candidates = await snapshotStore.GetAggregatesNeedingRefreshAsync(
            staleBeforeUtc, MaxSeriesPerRun, cancellationToken);

        if (candidates.Count == 0)
        {
            logger.LogInformation("UpdateTvDbInfoTimer: no keep-updated series are due for a refresh.");
            return;
        }

        logger.LogInformation(
            "UpdateTvDbInfoTimer: refreshing {Count} series not updated since {StaleBefore:u} (cap {Cap}).",
            candidates.Count, staleBeforeUtc, MaxSeriesPerRun);

        var refreshed = 0;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await theTvDbService.RefreshSeriesAggregateByIdAsync(candidate.TvdbId, cancellationToken))
                    refreshed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad series shouldn't abort the batch — it'll be retried next run.
                logger.LogWarning(ex, "UpdateTvDbInfoTimer: failed to refresh series {SeriesId}.", candidate.TvdbId);
            }
        }

        logger.LogInformation(
            "UpdateTvDbInfoTimer: refreshed {Refreshed}/{Total} series.", refreshed, candidates.Count);
    }
}
