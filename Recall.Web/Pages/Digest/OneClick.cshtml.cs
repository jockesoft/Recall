using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services.Digest;

namespace Recall.Web.Pages.Digest;

/// <summary>
/// The address in a digest's <c>List-Unsubscribe</c> header. A mail client's
/// "Unsubscribe" button POSTs here on its own (RFC 8058,
/// <c>List-Unsubscribe-Post: List-Unsubscribe=One-Click</c>), with no cookies
/// and no form of ours. So this page, and only this page, accepts a POST
/// without an antiforgery token: the signed token in the URL is the
/// authorisation, and all it can do is switch one account's weekly email off.
/// A person who opens the address in a browser (a GET) is sent to the ordinary
/// confirmation page; a GET changes nothing.
/// </summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[EnableRateLimiting(InfrastructureServiceCollectionExtensions.DigestUnsubscribePolicy)]
public sealed class OneClickModel(
    IDigestUnsubscribeTokens tokens,
    IAppUserRepository userRepository,
    ILogger<OneClickModel> logger) : PageModel
{
    public IActionResult OnGet(string? token) => RedirectToPage("/Digest/Unsubscribe", new { token });

    public async Task<IActionResult> OnPostAsync(string? token, CancellationToken cancellationToken)
    {
        if (!tokens.TryRead(token, out var userId))
            return BadRequest();

        // The same answer whether or not the account still exists.
        await userRepository.SetDigestOptInAsync(userId, optedIn: false, cancellationToken);
        logger.LogInformation("Weekly digest switched off by a one-click unsubscribe for user {UserId}.", userId);

        return Content("Unsubscribed.", "text/plain");
    }
}
