using Recall.Web.Services.Notifications.Models;

namespace Recall.Web.Services.Notifications;

/// <summary>
/// In-app notifications. Today it only raises "a new episode aired" alerts, but
/// the shape (typed create methods + a generic read/mark surface) is meant to
/// take more notification kinds later.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Raises a single "new episode(s) of X" notification for the user, covering
    /// every episode in <paramref name="digest"/> they haven't already been
    /// notified about. A full-season drop becomes one alert, not one per episode.
    /// Returns <c>true</c> when a notification was created, <c>false</c> when
    /// there was nothing new to tell them.
    /// </summary>
    Task<bool> NotifyNewEpisodesAsync(Guid userId, NewEpisodesDigest digest, CancellationToken cancellationToken = default);

    /// <summary>The user's most recent notifications, newest first.</summary>
    Task<IReadOnlyList<NotificationListItem>> GetRecentAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Unread count for the navbar bell badge.</summary>
    Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the notification read and returns the local URL it points at (e.g.
    /// the episode details page), or <c>null</c> when it has no target or isn't
    /// the user's.
    /// </summary>
    Task<string?> OpenAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken = default);

    /// <summary>Marks all of the user's notifications read.</summary>
    Task MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default);
}
