using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Services.Authentication;
using Recall.Web.Services.Metrics;
using Recall.Web.Services.PendingActions;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Pages.Account;

/// <summary>
/// Landing page for the link in the sign-in email: redeems the token and, on
/// success, issues the auth cookie, does the action the visitor pressed before
/// signing in (when this browser has it) and returns to the page stored on the
/// token (<see cref="SignInCompletion"/>). Renders only in the failure case.
/// </summary>
[AllowAnonymous]
public sealed class VerifyModel(
    IPasswordlessAuthService authService,
    IPendingActions pendingActions,
    IPendingActionService pendingActionService,
    ISiteMetrics metrics,
    TimeProvider timeProvider) : PageModel
{
    public async Task<IActionResult> OnGetAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Page();

        var result = await authService.RedeemAsync(token, cancellationToken);
        if (!result.Succeeded)
            return Page();

        return await SignInCompletion.FinishAsync(
            this, result, pendingActions, pendingActionService, metrics, AirDate.Today(timeProvider), cancellationToken);
    }
}
