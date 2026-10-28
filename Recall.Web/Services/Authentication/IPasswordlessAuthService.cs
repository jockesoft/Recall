namespace Recall.Web.Services.Authentication;

/// <summary>
/// Passwordless (magic-link) sign-in. A request mints a single-use token and
/// queues an email containing a link back to the app; redeeming that link
/// verifies the token and identifies the user so a cookie can be issued.
/// </summary>
public interface IPasswordlessAuthService
{
    /// <summary>
    /// Creates a sign-in token for <paramref name="email"/> (provisioning the
    /// account on first use) and queues the sign-in email. The message body
    /// contains the URL returned by <paramref name="loginLinkFactory"/> for the
    /// generated raw token. Always completes the same way whether or not the
    /// account already existed, so it can't be used to probe for members.
    /// </summary>
    Task RequestLoginAsync(
        string email,
        Func<string, string> loginLinkFactory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a raw token from a sign-in link. On success the token is consumed
    /// (single-use) and the associated user is returned.
    /// </summary>
    Task<LoginRedemptionResult> RedeemAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies the 6-digit code from the sign-in email for <paramref name="email"/>
    /// (spaces ignored). On success the token is consumed, so the link stops
    /// working too. A wrong code, an expired one and an unknown address give the
    /// same <see cref="LoginRedemptionStatus.InvalidOrExpired"/>; after
    /// <see cref="SignInCode.MaxWrongAttempts"/> wrong codes for the address the
    /// answer is <see cref="LoginRedemptionStatus.TooManyAttempts"/> until a new
    /// email is requested.
    /// </summary>
    Task<LoginRedemptionResult> RedeemCodeAsync(string? email, string? code, CancellationToken cancellationToken = default);
}
