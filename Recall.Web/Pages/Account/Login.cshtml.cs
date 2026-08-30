using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Recall.Web.Services.Authentication;

namespace Recall.Web.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting("login-email")]
public sealed class LoginModel(IPasswordlessAuthService authService, ILogger<LoginModel> logger) : PageModel
{
    [BindProperty]
    [Required]
    [EmailAddress]
    [StringLength(320)]
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Honeypot. Hidden from users; automated form-fillers tend to populate any
    /// field named like this. A non-empty value means "bot" — we no-op.
    /// </summary>
    [BindProperty]
    public string? Website { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>True once a link has been sent, so the view shows the "check your inbox" state.</summary>
    public bool LinkSent { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(Website))
        {
            // Honeypot tripped — look identical to a real submission, do nothing.
            logger.LogWarning("Login honeypot tripped from {RemoteIp}.", HttpContext.Connection.RemoteIpAddress);
            LinkSent = true;
            return Page();
        }

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
