//-----------------------------------------------------------------------
// <copyright file="UpdateTvDbInfoTimer.cs" company="Kevant Development">
//     Copyright (c) Kevant Development. All rights reserved.
// </copyright>
// <author>Joakim Fredlund</author>
//-----------------------------------------------------------------------

using Microsoft.Extensions.Options;
using Quartz;
using Recall.Web.Infrastructure.External.TheTvDb;
using Recall.Web.Infrastructure.Persistence.TvdbCache;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Web.Infrastructure.Timers;

/// <summary>
/// Keeps the local TheTVDB snapshots fresh. Each run refreshes up to
/// <see cref="MaxSeriesPerRun"/> <c>cached_series_aggregate</c> rows — those
/// carrying TheTVDB's <c>keep_updated</c> flag once older than
/// <see cref="MinRefreshAge"/>, and every other row once older than
/// <see cref="SettledMaxAge"/> (an ended show still gets corrections, artwork,
/// or the occasional revival), and ahead of both, within the same cap, rows
/// cached by an older version of the mapping (today: before series carried
/// TheTVDB's genres), until none are left —
/// then up to <see cref="MaxEpisodesPerRun"/> <c>cached_episode_extended</c> rows
/// that are either older than <see cref="EpisodeMaxAge"/>, still titled "TBA" and
/// older than <see cref="TbaEpisodeMaxAge"/>, or aired without a still image and
/// due a recheck (<see cref="StillRecheck"/>: daily for 30 days after airing,
/// weekly up to 90, then no more, so an episode that will never get art is not
/// polled forever; and not at all for a series that rarely has stills). Refreshing episodes here keeps per-episode
/// data (title, air date, still) from drifting out of sync with the series
/// aggregate. The age checks mean the job can be scheduled far more often than
/// the refresh cadence without hammering the upstream API.
/// </summary>
[DisallowConcurrentExecution]
public class UpdateTvDbInfoTimer(
    ITvdbSnapshotStore snapshotStore,
    ITheTvDbService theTvDbService,
    IOptions<TheTvDbOptions> options,
    ILogger<UpdateTvDbInfoTimer> logger) : IJob
{
    /// <summary>Don't re-fetch a series from TheTVDB more often than this.</summary>
    private static readonly TimeSpan MinRefreshAge = TimeSpan.FromHours(12);

    /// <summary>
    /// Re-fetch a series that is not flagged <c>keep_updated</c> at least this
    /// often. Without it such a row would be served unchanged forever — the
    /// Postgres tier has no staleness check on read.
    /// </summary>
    private static readonly TimeSpan SettledMaxAge = TimeSpan.FromDays(30);

    /// <summary>Re-fetch a cached episode at least this often.</summary>
    private static readonly TimeSpan EpisodeMaxAge = TimeSpan.FromDays(30);

    /// <summary>
    /// Chase a still-"TBA" episode far more aggressively — its real title and air
    /// date usually land within a day or two of the placeholder.
    /// </summary>
    private static readonly TimeSpan TbaEpisodeMaxAge = TimeSpan.FromHours(12);

    /// <summary>Upper bound on series refreshed per run — deliberately low to start.</summary>
    private const int MaxSeriesPerRun = 10;

    /// <summary>Upper bound on episodes refreshed per run.</summary>
    private const int MaxEpisodesPerRun = 25;

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        // What the run cost TheTVDB, logged every time so the number can be
        // watched in production. A series refresh is a fixed few requests
        // whatever its episode count (see TheTvDbApiClient); an episode refresh
        // is two. A total far above 3 x series + 2 x episodes means a
        // per-episode request has crept back into the series refresh.
        using var run = TheTvDbRequestMeter.Start();

        int series, seriesRequests;
        using (var meter = TheTvDbRequestMeter.Start())
        {
            series = await RefreshStaleAggregatesAsync(cancellationToken);
            seriesRequests = meter.Count;
        }

        int episodes, episodeRequests;
        using (var meter = TheTvDbRequestMeter.Start())
        {
            episodes = await RefreshStaleEpisodesAsync(cancellationToken);
            episodeRequests = meter.Count;
        }

        logger.LogInformation(
            "UpdateTvDbInfoTimer: {Requests} TheTVDB request(s) this run: {SeriesRequests} for {Series} series, {EpisodeRequests} for {Episodes} cached episode(s).",
            run.Count, seriesRequests, series, episodeRequests, episodes);
    }

    /// <returns>How many series the run tried to refresh.</returns>
    private async Task<int> RefreshStaleAggregatesAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var staleBeforeUtc = now - MinRefreshAge;

        var candidates = await snapshotStore.GetAggregatesNeedingRefreshAsync(
            staleBeforeUtc, now - SettledMaxAge, MaxSeriesPerRun, cancellationToken);

        if (candidates.Count == 0)
        {
            logger.LogInformation("UpdateTvDbInfoTimer: no series are due for a refresh.");
            return 0;
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

        return candidates.Count;
    }

    /// <returns>How many cached episodes the run tried to refresh.</returns>
    private async Task<int> RefreshStaleEpisodesAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var staleBeforeUtc = now - EpisodeMaxAge;
        var tbaStaleBeforeUtc = now - TbaEpisodeMaxAge;
        var stillRecheck = new StillRecheck(now)
        {
            MinStillPercent = options.Value.StillRecheckMinStillPercent,
            MinAiredEpisodes = options.Value.StillRecheckMinAiredEpisodes
        };

        var candidates = await snapshotStore.GetEpisodesNeedingRefreshAsync(
            staleBeforeUtc, tbaStaleBeforeUtc, stillRecheck, MaxEpisodesPerRun, cancellationToken);

        if (candidates.Count == 0)
        {
            logger.LogInformation("UpdateTvDbInfoTimer: no cached episodes are due for a refresh.");
            return 0;
        }

        logger.LogInformation(
            "UpdateTvDbInfoTimer: refreshing {Count} cached episode(s): stale before {StaleBefore:u}, TBA before {TbaBefore:u}, or aired without a still and due a recheck (cap {Cap}).",
            candidates.Count, staleBeforeUtc, tbaStaleBeforeUtc, MaxEpisodesPerRun);

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

        return candidates.Count;
    }
}
