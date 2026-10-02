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
    ITheTvDbService theTvDbService,
    ILogger<NotificationsModel> logger) : PageModel
{
    public IReadOnlyList<NotificationListItem> Notifications { get; private set; } = Array.Empty<NotificationListItem>();

    private IReadOnlyDictionary<int, string> _posterBySeries = new Dictionary<int, string>();

    /// <summary>The poster of the series a notification is about; null when it has none or the series couldn't be read.</summary>
    public string? PosterFor(NotificationListItem notification) =>
        notification.SeriesTvdbId is { } seriesId && _posterBySeries.TryGetValue(seriesId, out var url) ? url : null;

    public int UnreadCount => Notifications.Count(n => !n.IsRead);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
            return;

        try
        {
            Notifications = await notificationService.GetRecentAsync(userId, cancellationToken);
            _posterBySeries = await LoadPostersAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not load notifications for the account page.");
            this.SetErrorToast("Could not load your notifications right now.");
        }
    }

    /// <summary>
    /// One (cached) aggregate per distinct series, best effort: a series that
    /// can't be read just gets the icon instead of its poster.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, string>> LoadPostersAsync(CancellationToken cancellationToken)
    {
        var seriesIds = Notifications
            .Select(n => n.SeriesTvdbId)
            .Where(id => id is > 0)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var aggregates = await Task.WhenAll(
            seriesIds.Select(id => theTvDbService.TryGetSeriesAggregateAsync(id, logger, nameof(NotificationsModel), cancellationToken)));

        return aggregates
            .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.ImageUrl))
            .ToDictionary(a => a!.TvdbId, a => a!.ImageUrl!);
    }

    /// <summary>The row's small "Mark read" button: read, without leaving the list.</summary>
    public async Task<IActionResult> OnPostMarkReadAsync(Guid id, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
            return RedirectToPage();

        try
        {
            await notificationService.MarkReadAsync(userId, id, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not mark notification {NotificationId} read.", id);
            this.SetErrorToast("Could not update your notifications right now.");
        }

        return RedirectToPage();
    }

    /// <summary>
    /// Marks the notification read, then sends the user to whatever it points at
    /// (e.g. the episode page). Falls back to the list when there's no target.
    /// POST, not GET: it changes state, and a GET can be triggered by a link
    /// prefetcher or another site without the user ever clicking.
    /// </summary>
    public async Task<IActionResult> OnPostOpenAsync(Guid id, CancellationToken cancellationToken)
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
