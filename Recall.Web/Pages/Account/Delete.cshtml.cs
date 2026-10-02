using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;

namespace Recall.Web.Pages.Account;

/// <summary>
/// "Delete my account". The GET is the confirmation page: what goes, that it
/// cannot be undone, and a field the user types their username (or email) into.
/// The POST checks that again on the server, deletes the account and everything
/// it owns in one transaction (<see cref="IAppUserRepository.DeleteAccountAsync"/>),
/// signs this browser out and goes to the landing page. Other browsers signed
/// in to the account stop working at their next cookie revalidation
/// (<c>RecallCookieEvents</c>, at most five minutes), because the user row is gone.
/// </summary>
[Authorize]
public sealed class DeleteModel(
    ICurrentUserService currentUser,
    IAppUserRepository userRepository,
    ILogger<DeleteModel> logger) : PageModel
{
    public const string ConfirmationMismatchMessage = "That doesn't match your username or email address.";

    /// <summary>What the user types to confirm: their username or their email address.</summary>
    [BindProperty]
    public string? Confirmation { get; set; }

    public string Username { get; private set; } = string.Empty;

    public string Email { get; private set; } = string.Empty;

    /// <summary>
    /// True when this account is the only admin. The page then explains why it
    /// cannot be deleted instead of offering the form.
    /// </summary>
    public bool IsOnlyAdmin { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
            return RedirectToPage("/Account/Login");

        return await LoadAsync(userId, cancellationToken) ? Page() : RedirectToPage("/Account/Login");
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
            return RedirectToPage("/Account/Login");

        if (!await LoadAsync(userId, cancellationToken))
            return RedirectToPage("/Account/Login");

        if (IsOnlyAdmin)
            return Page();

        if (!ConfirmationMatches())
        {
            ModelState.AddModelError(nameof(Confirmation), ConfirmationMismatchMessage);
            return Page();
        }

        AccountDeletionResult result;
        try
        {
            result = await userRepository.DeleteAccountAsync(userId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed deleting account {UserId}.", userId);
            this.SetErrorToast("Could not delete your account right now. Nothing was removed; please try again.");
            return Page();
        }

        if (result == AccountDeletionResult.OnlyAdmin)
        {
            // Became the only admin between loading the page and submitting it.
            IsOnlyAdmin = true;
            return Page();
        }

        // Deleted, or already gone (deleted from another session): either way
        // this browser's cookie is for an account that no longer exists.
        logger.LogInformation("Account {UserId} was deleted by its owner ({Result}).", userId, result);

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        this.SetSuccessToast("Your account and everything in it has been deleted.");
        return RedirectToPage("/Index");
    }

    private async Task<bool> LoadAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null)
            return false;

        Username = user.Username;
        Email = user.Email;
        IsOnlyAdmin = await userRepository.IsOnlyAdminAsync(userId, cancellationToken);
        return true;
    }

    /// <summary>The typed text is the username or the email address, ignoring case and surrounding spaces.</summary>
    private bool ConfirmationMatches()
    {
        var typed = Confirmation?.Trim();

        return !string.IsNullOrEmpty(typed)
               && (string.Equals(typed, Username, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(typed, Email, StringComparison.OrdinalIgnoreCase));
    }
}
