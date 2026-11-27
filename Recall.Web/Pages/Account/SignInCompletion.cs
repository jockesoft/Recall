using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Authentication;
using Recall.Web.Services.Authentication;
using Recall.Web.Services.PendingActions;

namespace Recall.Web.Pages.Account;

/// <summary>
/// The end of a sign-in, by the emailed link (Verify) or the code (Login):
/// the cookie, then the action the visitor pressed before signing in when this
/// browser still has it (<see cref="IPendingActions"/>), then back to the page
/// the token was asked for from.
/// </summary>
public static class SignInCompletion
{
    /// <summary>Where a sign-in without a (valid) return address ends.</summary>
    public const string DefaultReturnUrl = "/Dashboard";

    public static async Task<IActionResult> FinishAsync(
        PageModel page,
        LoginRedemptionResult result,
        IPendingActions pendingActions,
        IPendingActionService pendingActionService,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        await page.HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            RecallPrincipal.Create(result.UserId, result.DisplayName, result.Email, result.Role),
            new AuthenticationProperties { IsPersistent = true });

        // Taken (and the cookie removed) whatever happens next: an action is done once.
        if (pendingActions.Take(page.HttpContext) is { } action)
        {
            var outcome = await pendingActionService.CompleteAsync(result.UserId, action, cancellationToken);
            page.SetPendingActionToast(outcome, today);
        }

        // The token's address was checked when it was stored; check it again here.
        var target = !string.IsNullOrEmpty(result.ReturnUrl) && page.Url.IsLocalUrl(result.ReturnUrl)
            ? result.ReturnUrl
            : DefaultReturnUrl;

        return page.LocalRedirect(target);
    }
}
