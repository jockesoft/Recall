namespace Recall.Web.Services.Authentication;

/// <summary>
/// Server-side half of the Cloudflare Turnstile CAPTCHA on the sign-in form.
/// </summary>
public interface ITurnstileVerifier
{
    /// <summary>
    /// Verifies the token the Turnstile widget put in the form. Returns
    /// <c>true</c> when Turnstile isn't configured yet (see
    /// <see cref="Recall.Web.Infrastructure.Authentication.TurnstileOptions.IsConfigured"/>),
    /// so the form keeps working before keys are set up.
    /// </summary>
    Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken cancellationToken = default);
}
