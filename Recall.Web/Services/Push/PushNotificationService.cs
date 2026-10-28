using Microsoft.Extensions.Options;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Infrastructure.Push;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Services.Push;

public sealed class PushNotificationService(
    IPushSubscriptionRepository subscriptions,
    INotificationRepository notifications,
    IAppUserRepository users,
    IPushSender sender,
    IOptions<PushOptions> options,
    TimeProvider timeProvider,
    ILogger<PushNotificationService> logger) : IPushNotificationService
{
    public async Task<PushRunCounts> SendNewEpisodesAsync(Guid userId, DateTime sinceUtc, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
            return PushRunCounts.None;

        var devices = await subscriptions.GetForUserAsync(userId, cancellationToken);
        if (devices.Count == 0)
            return PushRunCounts.None;

        var quietHours = (await users.GetByIdAsync(userId, cancellationToken))?.PushQuietHours ?? true;
        var now = AirDate.Now(timeProvider);
        var counts = PushRunCounts.None;

        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var zone = DeviceTimeZone.Find(device.TimeZone);
                if (quietHours && QuietHours.IsQuiet(now, zone))
                {
                    await subscriptions.HoldAsync(device.Id, sinceUtc, QuietHours.NextRelease(now, zone), cancellationToken);
                    counts += new PushRunCounts(Held: 1);
                    continue;
                }

                // A device still holding last night's push gets one push for
                // both: everything unread since the hold began.
                var from = device.HeldSinceUtc is { } held && held < sinceUtc ? held : sinceUtc;
                var payload = PushSummary.For(await notifications.GetUnreadNewEpisodesSinceAsync(userId, from, cancellationToken));
                if (payload is null)
                    continue;

                counts += await DeliverAsync(device, payload, now, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Push to device {DeviceId} of user {UserId} failed.", device.Id, userId);
                counts += new PushRunCounts(Failed: 1);
            }
        }

        return counts;
    }

    public async Task<PushRunCounts> ReleaseHeldAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
            return PushRunCounts.None;

        var now = AirDate.Now(timeProvider);
        var counts = PushRunCounts.None;

        foreach (var device in await subscriptions.GetDueHeldAsync(now, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var unread = await notifications.GetUnreadNewEpisodesSinceAsync(
                    device.UserId, device.HeldSinceUtc ?? now, cancellationToken);

                // Read in the app overnight: the push has nothing left to say.
                if (PushSummary.For(unread) is not { } payload)
                {
                    await subscriptions.ClearHoldAsync(device.Id, cancellationToken);
                    counts += new PushRunCounts(Dropped: 1);
                    continue;
                }

                var delivery = await DeliverAsync(device, payload, now, cancellationToken);

                // A failed release is not retried hourly into the day: the
                // notifications are still waiting in the app.
                if (delivery.Failed > 0 && delivery.Removed == 0)
                    await subscriptions.ClearHoldAsync(device.Id, cancellationToken);

                counts += delivery;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Releasing the held push of device {DeviceId} failed.", device.Id);
                counts += new PushRunCounts(Failed: 1);
            }
        }

        return counts;
    }

    public async Task<PushRunCounts> SendTestAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
            return PushRunCounts.None;

        var payload = new PushPayload(
            "Recall test notification",
            "Notifications work on this device.",
            "/Account/Profile#push",
            PushPayload.AppIcon);
        var now = AirDate.Now(timeProvider);
        var counts = PushRunCounts.None;

        foreach (var device in await subscriptions.GetForUserAsync(userId, cancellationToken))
        {
            try
            {
                counts += await DeliverAsync(device, payload, now, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Test push to device {DeviceId} failed.", device.Id);
                counts += new PushRunCounts(Failed: 1);
            }
        }

        return counts;
    }

    private async Task<PushRunCounts> DeliverAsync(
        PushDevice device, PushPayload payload, DateTime now, CancellationToken cancellationToken)
    {
        // One topic per user: a push service keeps only the newest of them for
        // a device it cannot reach yet. A Guid in "N" form is the 32 URL-safe
        // characters the header allows.
        var delivery = await sender.SendAsync(
            new PushTarget(device.Endpoint, device.P256dh, device.Auth), payload, device.UserId.ToString("N"), cancellationToken);

        switch (delivery)
        {
            case PushDelivery.Delivered:
                await subscriptions.MarkDeliveredAsync(device.Id, now, cancellationToken);
                return new PushRunCounts(Sent: 1);

            case PushDelivery.Gone:
                await subscriptions.DeleteAsync(device.Id, cancellationToken);
                logger.LogInformation("Removed push device {DeviceId} of user {UserId}: the push service says it is gone.",
                    device.Id, device.UserId);
                return new PushRunCounts(Failed: 1, Removed: 1);

            default:
                return new PushRunCounts(Failed: 1);
        }
    }
}
