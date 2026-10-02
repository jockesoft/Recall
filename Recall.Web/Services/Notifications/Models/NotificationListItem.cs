using Recall.Web.Infrastructure.Display;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Services.Notifications.Models;

/// <summary>
/// One notification shaped for the Notifications page: the stored fields plus a
/// resolved deep-link target, an icon class and a short relative timestamp.
/// </summary>
public sealed record NotificationListItem(
    Guid Id,
    NotificationType Type,
    string Title,
    string? Body,
    int EpisodeCount,
    bool IsRead,
    DateTime CreatedUtc,
    string? TargetHref)
{
    /// <summary>CSS class of the row's leading icon.</summary>
    public string IconClass => Type switch
    {
        NotificationType.NewEpisode => Icons.Series,
        _ => Icons.Bell
    };

    /// <summary>"just now" / "5m ago" / "3h ago" / "2d ago" / "4w ago".</summary>
    public string RelativeTime => DisplayDate.Relative(DateTime.UtcNow - CreatedUtc);
}
