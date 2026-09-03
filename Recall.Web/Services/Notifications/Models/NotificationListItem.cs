using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Services.Notifications.Models;

/// <summary>
/// One notification shaped for the Notifications page: the stored fields plus a
/// resolved deep-link target, a Font Awesome icon class and a short relative
/// timestamp.
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
    /// <summary>Font Awesome classes for the row's leading icon.</summary>
    public string IconClass => Type switch
    {
        NotificationType.NewEpisode => "fa-regular fa-tv",
        _ => "fa-regular fa-bell"
    };

    /// <summary>"just now" / "5m ago" / "3h ago" / "2d ago" / "4w ago".</summary>
    public string RelativeTime => FormatRelative(DateTime.UtcNow - CreatedUtc);

    private static string FormatRelative(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";
        if (age < TimeSpan.FromDays(7)) return $"{(int)age.TotalDays}d ago";
        return $"{(int)(age.TotalDays / 7)}w ago";
    }
}
