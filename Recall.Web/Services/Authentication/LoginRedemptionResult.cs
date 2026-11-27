using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Services.Authentication;

public enum LoginRedemptionStatus
{
    /// <summary>The token was valid and has now been consumed.</summary>
    Success,

    /// <summary>The token was unknown, already used, or past its expiry; or the code was wrong, or the address unknown.</summary>
    InvalidOrExpired,

    /// <summary>
    /// Too many wrong codes for this address since its last sign-in email: the
    /// token (if there was one) is consumed, and only a new email helps.
    /// </summary>
    TooManyAttempts
}

/// <summary>
/// Outcome of redeeming a magic-link token. On <see cref="LoginRedemptionStatus.Success"/>
/// the identity fields are populated so the caller can issue an auth cookie.
/// </summary>
public sealed record LoginRedemptionResult(
    LoginRedemptionStatus Status,
    Guid UserId,
    string Email,
    string DisplayName,
    UserRole Role)
{
    /// <summary>Where the sign-in returns to, as stored on the token; null for the default. Check it with <c>Url.IsLocalUrl</c> before redirecting.</summary>
    public string? ReturnUrl { get; init; }

    public bool Succeeded => Status == LoginRedemptionStatus.Success;

    public static LoginRedemptionResult Invalid() =>
        new(LoginRedemptionStatus.InvalidOrExpired, Guid.Empty, string.Empty, string.Empty, UserRole.User);

    public static LoginRedemptionResult TooManyAttempts() =>
        new(LoginRedemptionStatus.TooManyAttempts, Guid.Empty, string.Empty, string.Empty, UserRole.User);

    public static LoginRedemptionResult ForUser(Guid userId, string email, string displayName, UserRole role, string? returnUrl = null) =>
        new(LoginRedemptionStatus.Success, userId, email, displayName, role) { ReturnUrl = returnUrl };
}
