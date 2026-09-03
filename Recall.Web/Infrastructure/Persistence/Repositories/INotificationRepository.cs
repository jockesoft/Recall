using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public interface INotificationRepository
{
    /// <summary>
    /// The user's notifications, newest first, capped at <paramref name="take"/>.
    /// </summary>
    Task<IReadOnlyList<Notification>> GetForUserAsync(
        Guid userId,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>A single notification owned by the user, or <c>null</c>.</summary>
    Task<Notification?> GetAsync(
        Guid userId,
        Guid notificationId,
        CancellationToken cancellationToken = default);

    /// <summary>How many of the user's notifications are still unread.</summary>
    Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Of <paramref name="episodeTvdbIds"/>, the ones the user has already been
    /// notified about (i.e. present in the <c>notified_episode</c> ledger).
    /// </summary>
    Task<IReadOnlySet<int>> GetAlreadyNotifiedEpisodeIdsAsync(
        Guid userId,
        IEnumerable<int> episodeTvdbIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts one "new episode(s)" notification plus a <c>notified_episode</c>
    /// ledger row for every id in <paramref name="coveredEpisodeTvdbIds"/>, in a
    /// single transaction. Returns <c>false</c> when a concurrent sweep already
    /// claimed one of those episodes (unique-violation on the ledger) — the end
    /// state is still "the user has been notified".
    /// </summary>
    Task<bool> AddNewEpisodeNotificationAsync(
        Guid userId,
        int seriesTvdbId,
        int linkEpisodeTvdbId,
        int episodeCount,
        string title,
        string? body,
        IReadOnlyCollection<int> coveredEpisodeTvdbIds,
        CancellationToken cancellationToken = default);

    /// <summary>Marks one notification read. No-op if it's missing or already read.</summary>
    Task MarkReadAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken = default);

    /// <summary>Marks every unread notification for the user read.</summary>
    Task MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>A single notification, flattened for read use.</summary>
/// <param name="Id">Notification id.</param>
/// <param name="Type">What the notification is about.</param>
/// <param name="Title">Short headline.</param>
/// <param name="Body">Optional supporting line.</param>
/// <param name="SeriesTvdbId">Series the notification concerns, when applicable.</param>
/// <param name="EpisodeTvdbId">Episode the notification links to, when applicable.</param>
/// <param name="EpisodeCount">How many episodes it covers (1 unless collapsed).</param>
/// <param name="IsRead">Whether the user has seen it.</param>
/// <param name="CreatedUtc">When it was raised.</param>
/// <param name="ReadUtc">When it was marked read, if it has been.</param>
public sealed record Notification(
    Guid Id,
    NotificationType Type,
    string Title,
    string? Body,
    int? SeriesTvdbId,
    int? EpisodeTvdbId,
    int EpisodeCount,
    bool IsRead,
    DateTime CreatedUtc,
    DateTime? ReadUtc);
