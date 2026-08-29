namespace Recall.Web.Services.Authentication;

public enum LoginRedemptionStatus
{
    /// <summary>The token was valid and has now been consumed.</summary>
    Success,

    /// <summary>The token was unknown, already used, or past its expiry.</summary>
    InvalidOrExpired
}

/// <summary>
/// Outcome of redeeming a magic-link token. On <see cref="LoginRedemptionStatus.Success"/>
/// the identity fields are populated so the caller can issue an auth cookie.
/// </summary>
public sealed record LoginRedemptionResult(
    LoginRedemptionStatus Status,
    Guid UserId,
    string Email,
    string DisplayName)
{
    public bool Succeeded => Status == LoginRedemptionStatus.Success;

    public static LoginRedemptionResult Invalid() =>
        new(LoginRedemptionStatus.InvalidOrExpired, Guid.Empty, string.Empty, string.Empty);

    public static LoginRedemptionResult ForUser(Guid userId, string email, string displayName) =>
        new(LoginRedemptionStatus.Success, userId, email, displayName);
}
