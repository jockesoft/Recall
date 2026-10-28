using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Authentication;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Infrastructure.Push;
using Recall.Web.Services;
using Recall.Web.Services.Push;

namespace Recall.Web.Pages.Admin;

[Authorize(Roles = Roles.Admin)]
public sealed class IndexModel(
    IAppUserRepository userRepository,
    ICurrentUserService currentUser,
    IPushNotificationService pushNotificationService,
    IOptions<PushOptions> pushOptions,
    ILogger<IndexModel> logger) : PageModel
{
    /// <summary>Total rows in <c>app_user</c> — every account that has ever signed in.</summary>
    public int RegisteredUserCount { get; private set; }

    /// <summary>How many of those accounts are admins.</summary>
    public int AdminCount { get; private set; }

    /// <summary>Push is enabled here, so the test button is shown.</summary>
    public bool PushAvailable => pushOptions.Value.Enabled;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var counts = await userRepository.GetCountsAsync(cancellationToken);

        RegisteredUserCount = counts.Total;
        AdminCount = counts.Admins;
    }

    /// <summary>
    /// "Send a test push to my devices": to the signed-in admin's own devices
    /// and nobody else's, ignoring quiet hours, so the whole chain (keys, push
    /// service, service worker) can be checked from a phone.
    /// </summary>
    public async Task<IActionResult> OnPostTestPushAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId || !PushAvailable)
            return RedirectToPage();

        try
        {
            var result = await pushNotificationService.SendTestAsync(userId, cancellationToken);
            var failed = result.Failed > 0 ? $" {result.Failed} failed{(result.Removed > 0 ? $", {result.Removed} of them no longer subscribed and removed" : string.Empty)}." : string.Empty;

            if (result.Sent > 0)
                this.SetSuccessToast($"Sent a test notification to {result.Sent} {(result.Sent == 1 ? "device" : "devices")}.{failed}");
            else if (result.Failed > 0)
                this.SetErrorToast($"No test notification could be sent.{failed}");
            else
                this.SetInfoToast("You have no devices with notifications on. Turn them on in your profile first.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Test push for admin {UserId} failed.", userId);
            this.SetErrorToast("Could not send the test notification right now.");
        }

        return RedirectToPage();
    }
}
