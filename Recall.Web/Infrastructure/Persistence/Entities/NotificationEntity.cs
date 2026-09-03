namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>What a <see cref="NotificationEntity"/> is about.</summary>
public enum NotificationType
{
    /// <summary>A new episode of a series the user tracks has aired.</summary>
    NewEpisode = 1
}

/// <summary>
/// One in-app notification for a single user. The row's presence is the
/// notification; <see cref="IsRead"/> only tracks whether the user has seen it.
/// Payload columns are deliberately explicit (rather than a JSON blob) so the
/// deep-link target can be built without a second lookup — mirrors
/// <see cref="UserLikeEntity"/>.
/// </summary>
public sealed class NotificationEntity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public AppUserEntity User { get; set; } = null!;

    public NotificationType Type { get; set; }

    /// <summary>Short headline, e.g. "New episode of Severance".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Optional supporting line, e.g. "S02E05 · Trojan's Horse is out now.".</summary>
    public string? Body { get; set; }

    /// <summary>TVDB id of the series this notification concerns, when applicable.</summary>
    public int? SeriesTvdbId { get; set; }

    /// <summary>
    /// TVDB id of the episode this notification links to. For
    /// <see cref="NotificationType.NewEpisode"/> that is the earliest episode of
    /// the batch it covers — the full set is recorded in <c>notified_episode</c>,
    /// which is also what keeps a re-run of the sweep from notifying twice.
    /// </summary>
    public int? EpisodeTvdbId { get; set; }

    /// <summary>
    /// How many episodes this notification stands in for. 1 for a single new
    /// episode; higher when a full-season drop was collapsed into one alert.
    /// </summary>
    public int EpisodeCount { get; set; } = 1;

    public bool IsRead { get; set; }
    public DateTime? ReadUtc { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
