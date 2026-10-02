using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services.Digest;

namespace Recall.Web.Pages.Digest;

/// <summary>
/// Where the "turn it off" link in a weekly digest leads. It works without
/// signing in: the signed token in the link says whose digest it is. The GET
/// only shows a button, because opening a link must not change anything (mail
/// scanners and link previews open links too); the button POSTs.
/// The answer is the same whether or not the account still exists.
/// </summary>
[AllowAnonymous]
[EnableRateLimiting(InfrastructureServiceCollectionExtensions.DigestUnsubscribePolicy)]
public sealed class UnsubscribeModel(
    IDigestUnsubscribeTokens tokens,
    IAppUserRepository userRepository,
    ILogger<UnsubscribeModel> logger) : PageModel
{
    public enum UnsubscribeState
    {
        /// <summary>The link is good: show the button.</summary>
        Confirm,

        /// <summary>The weekly email is off.</summary>
        Done,

        /// <summary>Not a link this site issued (or its key is gone).</summary>
        InvalidLink
    }

    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    public UnsubscribeState State { get; private set; }

    public void OnGet()
    {
        State = tokens.TryRead(Token, out _) ? UnsubscribeState.Confirm : UnsubscribeState.InvalidLink;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!tokens.TryRead(Token, out var userId))
        {
            State = UnsubscribeState.InvalidLink;
            return Page();
        }

        try
        {
            // False when the account no longer exists; the page says the same either way.
            await userRepository.SetDigestOptInAsync(userId, optedIn: false, cancellationToken);
            logger.LogInformation("Weekly digest switched off through an unsubscribe link for user {UserId}.", userId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed switching the weekly digest off for user {UserId}.", userId);
            this.SetErrorToast("Could not turn the weekly email off right now. Please try again.");
            State = UnsubscribeState.Confirm;
            return Page();
        }

        State = UnsubscribeState.Done;
        return Page();
    }
}
