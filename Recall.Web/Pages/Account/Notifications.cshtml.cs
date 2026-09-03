using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Extensions;
using Recall.Web.Services;
using Recall.Web.Services.Notifications;
using Recall.Web.Services.Notifications.Models;

namespace Recall.Web.Pages.Account;

[Authorize]
public sealed class NotificationsModel(
    ICurrentUserService currentUser,
    INotificationService notificationService,
    ILogger<NotificationsModel> logger) : PageModel
{
    public IReadOnlyList<NotificationListItem> Notifications { get; private set; } = Array.Empty<NotificationListItem>();

    public int UnreadCount => Notifications.Count(n => !n.IsRead);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
            return;

        try
        {
            Notifications = await notificationService.GetRecentAsync(userId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not load notifications for the account page.");
            this.SetErrorToast("Could not load your notifications right now.");
        }
    }

    /// <summary>
    /// Marks the notification read, then sends the user to whatever it points at
    /// (e.g. the episode page). Falls back to the list when there's no target.
    /// </summary>
    public async Task<IActionResult> OnGetOpenAsync(Guid id, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
            return RedirectToPage();

        try
        {
            var target = await notificationService.OpenAsync(userId, id, cancellationToken);
            if (!string.IsNullOrWhiteSpace(target))
                return LocalRedirect(target);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not open notification {NotificationId}.", id);
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostMarkAllReadAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
            return RedirectToPage();

        try
        {
            await notificationService.MarkAllReadAsync(userId, cancellationToken);
            this.SetInfoToast("All notifications marked as read.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not mark all notifications read.");
            this.SetErrorToast("Could not update your notifications right now.");
        }

        return RedirectToPage();
    }
}
