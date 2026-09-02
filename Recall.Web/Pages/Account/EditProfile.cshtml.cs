using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;

namespace Recall.Web.Pages.Account;

[Authorize]
public sealed partial class EditProfileModel(
    ICurrentUserService currentUser,
    IAppUserRepository userRepository,
    ILogger<EditProfileModel> logger) : PageModel
{
    /// <summary>Allowed username characters — letters, digits, dot, dash, underscore.</summary>
    public const string UsernamePattern = "^[A-Za-z0-9._-]+$";

    public const int UsernameMinLength = 4;
    public const int UsernameMaxLength = 30;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>The username currently on file, shown as the "unchanged" baseline.</summary>
    public string CurrentUsername { get; private set; } = string.Empty;

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Enter a username.")]
        [StringLength(UsernameMaxLength, MinimumLength = UsernameMinLength,
            ErrorMessage = "Username must be between {2} and {1} characters.")]
        [RegularExpression(UsernamePattern,
            ErrorMessage = "Only letters, numbers, dots, hyphens and underscores are allowed.")]
        [Display(Name = "Username")]
        public string Username { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
            return RedirectToPage("/Account/Login");

        var user = await userRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null)
            return RedirectToPage("/Account/Login");

        CurrentUsername = user.Username;
        Input.Username = user.Username;
        return Page();
    }

    /// <summary>
    /// Live availability check for the client-side validation. Returns
    /// { available, message } as JSON; never throws for a bad input, it just
    /// reports it as unavailable with a reason.
    /// </summary>
    public async Task<IActionResult> OnGetCheckUsernameAsync(string? username, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
            return new JsonResult(new { available = false, message = "You are not signed in." });

        var candidate = (username ?? string.Empty).Trim();

        if (candidate.Length is < UsernameMinLength or > UsernameMaxLength)
            return new JsonResult(new
            {
                available = false,
                message = $"Username must be between {UsernameMinLength} and {UsernameMaxLength} characters."
            });

        if (!UsernameRegex().IsMatch(candidate))
            return new JsonResult(new
            {
                available = false,
                message = "Only letters, numbers, dots, hyphens and underscores are allowed."
            });

        var user = await userRepository.GetByIdAsync(userId, cancellationToken);
        if (user is not null && string.Equals(user.Username, candidate, StringComparison.Ordinal))
            return new JsonResult(new { available = true, message = "This is your current username." });

        var available = await userRepository.IsUsernameAvailableAsync(candidate, userId, cancellationToken);
        return new JsonResult(new
        {
            available,
            message = available ? "That username is available." : "That username is already taken."
        });
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
            return RedirectToPage("/Account/Login");

        if (!ModelState.IsValid)
        {
            await LoadCurrentUsernameAsync(userId, cancellationToken);
            return Page();
        }

        var newUsername = Input.Username.Trim();
        var result = await userRepository.UpdateUsernameAsync(userId, newUsername, cancellationToken);

        switch (result)
        {
            case UsernameUpdateResult.Taken:
                ModelState.AddModelError("Input.Username", "That username is already taken.");
                await LoadCurrentUsernameAsync(userId, cancellationToken);
                return Page();

            case UsernameUpdateResult.UserNotFound:
                logger.LogWarning("Edit profile: no user row for authenticated id {UserId}.", userId);
                return RedirectToPage("/Account/Login");
        }

        await RefreshDisplayNameClaimAsync(newUsername);

        logger.LogInformation("User {UserId} changed their username.", userId);
        this.SetSuccessToast("Your username has been updated.");
        return RedirectToPage("/Account/Profile");
    }

    private async Task LoadCurrentUsernameAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(userId, cancellationToken);
        CurrentUsername = user?.Username ?? string.Empty;
    }

    /// <summary>
    /// Re-issues the auth cookie with the new name claim so the change shows
    /// immediately everywhere it's read from the principal (navbar, profile),
    /// without forcing the user to sign in again.
    /// </summary>
    private async Task RefreshDisplayNameClaimAsync(string username)
    {
        if (User.Identity is not ClaimsIdentity current)
            return;

        var claims = current.Claims
            .Where(c => c.Type != ClaimTypes.Name && c.Type != "name")
            .Append(new Claim(ClaimTypes.Name, username))
            .ToList();

        var identity = new ClaimsIdentity(
            claims,
            CookieAuthenticationDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            current.RoleClaimType);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    [GeneratedRegex(UsernamePattern)]
    private static partial Regex UsernameRegex();
}
