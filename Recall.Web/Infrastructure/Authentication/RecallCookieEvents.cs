using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Web.Infrastructure.Authentication;

/// <summary>
/// Re-checks a signed-in cookie against the user row every
/// <see cref="RevalidationInterval"/>. The cookie lives for 30 sliding days and
/// carries the role, so without this a deleted account kept working and a
/// demoted admin stayed an admin until the cookie happened to expire.
///
/// A missing user is signed out; a changed username, email or role is written
/// into a fresh cookie. The time of the last check travels inside the cookie's
/// own properties, so there is no server-side session state to keep.
/// </summary>
public sealed class RecallCookieEvents(
    IAppUserRepository userRepository,
    TimeProvider timeProvider,
    ILogger<RecallCookieEvents> logger) : CookieAuthenticationEvents
{
    /// <summary>
    /// How long a cookie is trusted between checks — i.e. the longest a
    /// deletion or role change can take to bite. One indexed primary-key read
    /// per signed-in user per interval.
    /// </summary>
    public static readonly TimeSpan RevalidationInterval = TimeSpan.FromMinutes(5);

    public const string LastValidatedKey = "recall.validated-utc";

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var now = timeProvider.GetUtcNow();

        if (ValidatedRecently(context.Properties, now))
            return;

        var principal = context.Principal;
        if (!Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            await RejectAsync(context);
            return;
        }

        Persistence.Entities.AppUserEntity? user;
        try
        {
            user = await userRepository.GetByIdAsync(userId, context.HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A database blip must not sign everyone out. Keep the session as it
            // is and leave the timestamp alone, so the next request tries again.
            logger.LogWarning(ex, "Could not revalidate the auth cookie for user {UserId}; keeping the session.", userId);
            return;
        }

        if (user is null)
        {
            logger.LogInformation("Auth cookie rejected: user {UserId} no longer exists.", userId);
            await RejectAsync(context);
            return;
        }

        if (IsOutOfDate(principal!, user.Username, user.Email, user.Role.ToString()))
        {
            logger.LogInformation("Auth cookie for user {UserId} refreshed from the user row (role {Role}).", userId, user.Role);
            context.ReplacePrincipal(RecallPrincipal.Create(user.Id, user.Username, user.Email, user.Role));
        }

        context.Properties.Items[LastValidatedKey] = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        context.ShouldRenew = true;
    }

    private static bool ValidatedRecently(AuthenticationProperties properties, DateTimeOffset now) =>
        properties.Items.TryGetValue(LastValidatedKey, out var raw)
        && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)
        && last <= now
        && now - last < RevalidationInterval;

    private static bool IsOutOfDate(ClaimsPrincipal principal, string username, string email, string role) =>
        !string.Equals(principal.FindFirstValue(ClaimTypes.Name), username, StringComparison.Ordinal)
        || !string.Equals(principal.FindFirstValue(ClaimTypes.Email), email, StringComparison.Ordinal)
        || !string.Equals(principal.FindFirstValue(ClaimTypes.Role), role, StringComparison.Ordinal);

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(context.Scheme.Name);
    }
}
