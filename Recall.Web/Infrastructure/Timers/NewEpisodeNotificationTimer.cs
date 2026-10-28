//-----------------------------------------------------------------------
// <copyright file="NewEpisodeNotificationTimer.cs" company="Kevant Development">
//     Copyright (c) Kevant Development. All rights reserved.
// </copyright>
// <author>Joakim Fredlund</author>
//-----------------------------------------------------------------------

using Quartz;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.WatchTracking;
using Recall.Web.Services.Notifications;
using Recall.Web.Services.Notifications.Models;
using Recall.Web.Services.Push;

namespace Recall.Web.Infrastructure.Timers;

/// <summary>
/// Raises "a new episode aired" notifications. Scheduled every hour in
/// <c>AddScheduledJobs</c>, so a notification follows an episode's release
/// moment (<see cref="EpisodeRelease"/>) within the hour. Each run walks every
/// series at least one user tracks, reads its aggregate from the caches only
/// (no TheTVDB or OMDb request, which is what makes the hourly cadence free),
/// and for each episode released within <see cref="Lookback"/> notifies every tracking user who hasn't already marked
/// that episode watched. A user who stopped watching the series is not notified. All of a series' new episodes for one user collapse
/// into a single notification, so a full-season drop is one alert, not eight.
/// The <c>notified_episode</c> ledger makes reruns idempotent, so there is no
/// watermark to keep and no historical backfill — only the last few days ever
/// produce a notification.
///
/// It also sends the push notifications (<see cref="IPushNotificationService"/>):
/// first the pushes held for quiet hours whose 08:00 has come, then, once all
/// of a run's notifications exist, one push per device for each user who got
/// new ones. Pushing comes after creating and in its own try/catch, so a push
/// failure never costs a notification.
/// </summary>
[DisallowConcurrentExecution]
public sealed class NewEpisodeNotificationTimer(
    ITrackedSeriesRepository trackedSeriesRepository,
    IEpisodeWatchRepository episodeWatchRepository,
    ITheTvDbService theTvDbService,
    INotificationService notificationService,
    IPushNotificationService pushNotificationService,
    TimeProvider timeProvider,
    ILogger<NewEpisodeNotificationTimer> logger) : IJob
{
    /// <summary>How far back an air date can be and still trigger a notification.</summary>
    private static readonly TimeSpan Lookback = TimeSpan.FromDays(3);

    /// <summary>Safety cap on series per run — aggregate reads are cache-first, so this is generous.</summary>
    private const int MaxSeriesPerRun = 500;

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        // Released (EpisodeRelease) within the look-back: a US evening episode is
        // notified once it has aired there, not from midnight UTC of its air date.
        var now = AirDate.Now(timeProvider);
        var earliestRelease = now - Lookback;

        // Compared with notification.created_utc, which SaveChanges stamps from
        // the system clock, so it is read from the same clock.
        var runStartedUtc = DateTime.UtcNow;

        var pushes = await ReleaseHeldPushesAsync(cancellationToken);

        var seriesIds = await trackedSeriesRepository.GetDistinctTrackedTvdbIdsAsync(cancellationToken);
        if (seriesIds.Count == 0)
        {
            logger.LogInformation("NewEpisodeNotificationTimer: no tracked series — nothing to check.");
            LogPushes(pushes);
            return;
        }

        if (seriesIds.Count > MaxSeriesPerRun)
        {
            logger.LogWarning(
                "NewEpisodeNotificationTimer: {Total} tracked series exceeds the per-run cap of {Cap}; the rest wait for the next run.",
                seriesIds.Count, MaxSeriesPerRun);
        }

        var createdTotal = 0;
        var seriesWithNewEpisodes = 0;
        var notifiedUsers = new HashSet<Guid>();

        foreach (var seriesId in seriesIds.Take(MaxSeriesPerRun))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // From the caches only (Redis, then the Postgres snapshot), never
                // TheTVDB: the job makes no external request, which is what lets
                // it run every hour. A series that is not cached waits for the
                // refresh job (or a page) to cache it.
                var aggregate = await theTvDbService.GetCachedSeriesAggregateAsync(seriesId, cancellationToken);
                if (aggregate is null)
                    continue;

                var recentEpisodes = aggregate.Episodes
                    .Where(e => e.IsMovie != true
                                && EpisodeRelease.MomentUtc(e.Aired, aggregate.AirsTime, aggregate.OriginalCountry) is { } release
                                && release.IsReleasedBy(now)
                                && release.Utc > earliestRelease)
                    .ToList();

                if (recentEpisodes.Count == 0)
                    continue;

                // Not the users who stopped watching it: a new episode is no news to them.
                var userIds = await trackedSeriesRepository.GetUserIdsTrackingAsync(seriesId, cancellationToken);
                if (userIds.Count == 0)
                    continue;

                seriesWithNewEpisodes++;

                foreach (var userId in userIds)
                {
                    var watchedIds = await episodeWatchRepository.GetWatchedEpisodeIdsAsync(
                        userId, seriesId, cancellationToken);

                    var candidates = recentEpisodes
                        .Where(e => !watchedIds.Contains(e.Id))
                        .Select(e => new NewEpisodeItem(e.Id, e.SeasonNumber, e.EpisodeNumber, e.Name))
                        .ToList();

                    if (candidates.Count == 0)
                        continue;

                    var digest = new NewEpisodesDigest(aggregate.TvdbId, aggregate.Name, candidates);

                    if (await notificationService.NotifyNewEpisodesAsync(userId, digest, cancellationToken))
                    {
                        createdTotal++;
                        notifiedUsers.Add(userId);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad series shouldn't abort the sweep — it's retried next run.
                logger.LogWarning(ex, "NewEpisodeNotificationTimer: failed while checking series {SeriesId}.", seriesId);
            }
        }

        logger.LogInformation(
            "NewEpisodeNotificationTimer: created {Created} notification(s) across {SeriesCount} series with recent episodes.",
            createdTotal, seriesWithNewEpisodes);

        // One push per device per user, summarising all of this run's
        // notifications for them (several series become one push).
        foreach (var userId in notifiedUsers)
        {
            try
            {
                pushes += await pushNotificationService.SendNewEpisodesAsync(userId, runStartedUtc, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "NewEpisodeNotificationTimer: pushing to user {UserId} failed.", userId);
                pushes += new PushRunCounts(Failed: 1);
            }
        }

        LogPushes(pushes);
    }

    private async Task<PushRunCounts> ReleaseHeldPushesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await pushNotificationService.ReleaseHeldAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "NewEpisodeNotificationTimer: releasing held pushes failed; they are retried next run.");
            return PushRunCounts.None;
        }
    }

    private void LogPushes(PushRunCounts pushes) =>
        logger.LogInformation(
            "NewEpisodeNotificationTimer: {Sent} push(es) sent, {Held} held for quiet hours, {Failed} failed " +
            "({Removed} expired subscription(s) removed, {Dropped} held push(es) dropped as already read).",
            pushes.Sent, pushes.Held, pushes.Failed, pushes.Removed, pushes.Dropped);
}
