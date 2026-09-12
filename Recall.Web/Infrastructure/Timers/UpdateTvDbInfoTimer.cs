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
/// Keeps the local TheTVDB snapshots fresh. Each run refreshes up to
/// <see cref="MaxSeriesPerRun"/> <c>cached_series_aggregate</c> rows that carry
/// TheTVDB's <c>keep_updated</c> flag and are older than <see cref="MinRefreshAge"/>,
/// then up to <see cref="MaxEpisodesPerRun"/> <c>cached_episode_extended</c> rows
/// that are either older than <see cref="EpisodeMaxAge"/>, still titled "TBA" and
/// older than <see cref="TbaEpisodeMaxAge"/>, or missing their still image despite
/// having already aired (re-checked every <see cref="MinRefreshAge"/>, up to
/// <see cref="MaxImageChaseAttempts"/> times, so an episode that will never get
/// art doesn't get polled forever). Refreshing episodes here keeps per-episode
/// data (title, air date, still) from drifting out of sync with the series
/// aggregate. The age checks mean the job can be scheduled far more often than
/// the refresh cadence without hammering the upstream API.
/// </summary>
[DisallowConcurrentExecution]
public class UpdateTvDbInfoTimer(
    ITvdbSnapshotStore snapshotStore,
    ITheTvDbService theTvDbService,
    ILogger<UpdateTvDbInfoTimer> logger) : IJob
{
    /// <summary>Don't re-fetch a series from TheTVDB more often than this.</summary>
    private static readonly TimeSpan MinRefreshAge = TimeSpan.FromHours(12);

    /// <summary>Re-fetch a cached episode at least this often.</summary>
    private static readonly TimeSpan EpisodeMaxAge = TimeSpan.FromDays(30);

    /// <summary>
    /// Chase a still-"TBA" episode far more aggressively — its real title and air
    /// date usually land within a day or two of the placeholder.
    /// </summary>
    private static readonly TimeSpan TbaEpisodeMaxAge = TimeSpan.FromHours(12);

    /// <summary>
    /// Minimum time between re-checks of an aired episode that's still missing
    /// its still image — reuses <see cref="MinRefreshAge"/>'s cadence.
    /// </summary>
    private static readonly TimeSpan ImageChaseInterval = MinRefreshAge;

    /// <summary>
    /// Give up chasing an aired episode's missing still after this many
    /// consecutive imageless refreshes — some episodes simply never get art.
    /// </summary>
    private const int MaxImageChaseAttempts = 5;

    /// <summary>Upper bound on series refreshed per run — deliberately low to start.</summary>
    private const int MaxSeriesPerRun = 10;

    /// <summary>Upper bound on episodes refreshed per run.</summary>
    private const int MaxEpisodesPerRun = 25;

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        await RefreshStaleAggregatesAsync(cancellationToken);
        await RefreshStaleEpisodesAsync(cancellationToken);
    }

    private async Task RefreshStaleAggregatesAsync(CancellationToken cancellationToken)
    {
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

    private async Task RefreshStaleEpisodesAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var staleBeforeUtc = now - EpisodeMaxAge;
        var tbaStaleBeforeUtc = now - TbaEpisodeMaxAge;
        var imageChaseBeforeUtc = now - ImageChaseInterval;
        var today = DateOnly.FromDateTime(now);

        var candidates = await snapshotStore.GetEpisodesNeedingRefreshAsync(
            staleBeforeUtc, tbaStaleBeforeUtc, imageChaseBeforeUtc, today, MaxImageChaseAttempts,
            MaxEpisodesPerRun, cancellationToken);

        if (candidates.Count == 0)
        {
            logger.LogInformation("UpdateTvDbInfoTimer: no cached episodes are due for a refresh.");
            return;
        }

        logger.LogInformation(
            "UpdateTvDbInfoTimer: refreshing {Count} cached episode(s) — stale before {StaleBefore:u} / TBA before {TbaBefore:u} / image chase before {ImageChaseBefore:u} (cap {Cap}).",
            candidates.Count, staleBeforeUtc, tbaStaleBeforeUtc, imageChaseBeforeUtc, MaxEpisodesPerRun);

        var refreshed = 0;

        foreach (var episodeId in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await theTvDbService.RefreshEpisodeDetailsByIdAsync(episodeId, cancellationToken))
                    refreshed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad episode shouldn't abort the batch — it'll be retried next run.
                logger.LogWarning(ex, "UpdateTvDbInfoTimer: failed to refresh episode {EpisodeId}.", episodeId);
            }
        }

        logger.LogInformation(
            "UpdateTvDbInfoTimer: refreshed {Refreshed}/{Total} episodes.", refreshed, candidates.Count);
    }
}
