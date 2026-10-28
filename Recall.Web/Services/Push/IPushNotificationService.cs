namespace Recall.Web.Services.Push;

/// <summary>What a batch of pushes came to, for the job's log line.</summary>
/// <param name="Sent">Accepted by the push service.</param>
/// <param name="Held">Held for quiet hours, to go at 08:00 on the device.</param>
/// <param name="Failed">Not delivered, including to subscriptions that turned out to be gone.</param>
/// <param name="Removed">Subscriptions deleted because the push service said they no longer exist.</param>
/// <param name="Dropped">Held pushes not sent because every notification in them had been read.</param>
public sealed record PushRunCounts(int Sent = 0, int Held = 0, int Failed = 0, int Removed = 0, int Dropped = 0)
{
    public static readonly PushRunCounts None = new();

    public static PushRunCounts operator +(PushRunCounts a, PushRunCounts b) =>
        new(a.Sent + b.Sent, a.Held + b.Held, a.Failed + b.Failed, a.Removed + b.Removed, a.Dropped + b.Dropped);
}

/// <summary>
/// Push notifications for new episodes: one push per device summarising the
/// user's new notifications, held for quiet hours where the device's clock
/// says so. Does nothing while <c>Push:Enabled</c> is off. Never throws for a
/// failed push; the in-app notifications it summarises exist either way.
/// </summary>
public interface IPushNotificationService
{
    /// <summary>
    /// Pushes the user's unread new-episode notifications created since
    /// <paramref name="sinceUtc"/> to each of their devices: one push per
    /// device, or a hold until 08:00 there during quiet hours.
    /// </summary>
    Task<PushRunCounts> SendNewEpisodesAsync(Guid userId, DateTime sinceUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the held pushes that are due (08:00 has come on the device, or
    /// the user turned quiet hours off), each summarising the notifications
    /// still unread; one whose notifications were all read is dropped.
    /// </summary>
    Task<PushRunCounts> ReleaseHeldAsync(CancellationToken cancellationToken = default);

    /// <summary>A test push to every device of the user, whatever the time (Admin).</summary>
    Task<PushRunCounts> SendTestAsync(Guid userId, CancellationToken cancellationToken = default);
}
