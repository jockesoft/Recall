using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Infrastructure.Authentication;
using Recall.Web.Services.Authentication;

namespace Recall.Web.Pages.Account;

/// <summary>
/// Landing page for the link in the sign-in email: redeems the token and, on
/// success, issues the auth cookie and redirects on. Renders only in the failure
/// case.
/// </summary>
[AllowAnonymous]
public sealed class VerifyModel(IPasswordlessAuthService authService) : PageModel
{
    public async Task<IActionResult> OnGetAsync(
        string? token,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Page();

        var result = await authService.RedeemAsync(token, cancellationToken);
        if (!result.Succeeded)
            return Page();

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            RecallPrincipal.Create(result.UserId, result.DisplayName, result.Email, result.Role),
            new AuthenticationProperties { IsPersistent = true });

        var target = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl! : "/Dashboard";
        return LocalRedirect(target);
    }
}
