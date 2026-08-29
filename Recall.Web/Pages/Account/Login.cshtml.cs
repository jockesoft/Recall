using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Services.Authentication;

namespace Recall.Web.Pages.Account;

[AllowAnonymous]
public sealed class LoginModel(IPasswordlessAuthService authService) : PageModel
{
    [BindProperty]
    [Required]
    [EmailAddress]
    [StringLength(320)]
    public string Email { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>True once a link has been sent, so the view shows the "check your inbox" state.</summary>
    public bool LinkSent { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return Page();

        var scheme = Request.Scheme;
        var returnUrl = SafeReturnUrl();

        await authService.RequestLoginAsync(
            Email,
            token => Url.Page(
                "/Account/Verify",
                pageHandler: null,
                values: new { token, returnUrl },
                protocol: scheme)!,
            cancellationToken);

        // Always report the same thing whether or not the account existed.
        LinkSent = true;
        return Page();
    }

    private string SafeReturnUrl() =>
        !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/";
}
