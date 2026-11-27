using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Services.PendingActions;

namespace Recall.Web.Extensions;

public static class PendingActionPageExtensions
{
    /// <summary>
    /// What a Details page's handler does when a signed-out visitor presses one
    /// of its actions: keep the action in the pending-action cookie (and count
    /// the press), then send them to sign in, returning to the page the action
    /// is about. Nothing about the action is in the address.
    /// </summary>
    public static IActionResult SignInToFinish(this PageModel page, IPendingActions pendingActions, PendingAction action)
    {
        pendingActions.Start(page.HttpContext, action);
        return page.RedirectToPage("/Account/Login", new { returnUrl = action.ReturnUrl });
    }
}
