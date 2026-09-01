using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Services;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Pages.Account;

[Authorize]
public sealed class ProfileModel(
    ICurrentUserService currentUser,
    IWatchTimeService watchTimeService,
    ILogger<ProfileModel> logger) : PageModel
{
    public string DisplayName => currentUser.DisplayName ?? "—";

    public string Email => currentUser.Email ?? "—";

    public WatchTimeSummary WatchTime { get; private set; } = WatchTimeSummary.Empty;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
            return;

        try
        {
            WatchTime = await watchTimeService.GetTotalWatchTimeAsync(userId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not compute total watch time for the profile page.");
        }
    }
}
