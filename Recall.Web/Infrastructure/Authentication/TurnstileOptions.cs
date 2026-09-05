namespace Recall.Web.Infrastructure.Authentication;

/// <summary>
/// Bound from the <c>Turnstile</c> configuration section. Get a site key and
/// secret key for free at https://dash.cloudflare.com/?to=/:account/turnstile
/// — no domain migration to Cloudflare required. Put the secret key in
/// user-secrets (dev) or an environment variable (prod), never in
/// appsettings.
/// </summary>
public sealed class TurnstileOptions
{
    public const string SectionName = "Turnstile";

    /// <summary>Public key embedded in the login page's widget.</summary>
    public string? SiteKey { get; set; }

    /// <summary>Private key used server-side to verify a solved challenge.</summary>
    public string? SecretKey { get; set; }

    /// <summary>
    /// True once both keys are configured. While false the widget isn't
    /// rendered and verification is skipped, so the sign-in form keeps
    /// working (without the CAPTCHA layer) until you set the keys.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(SiteKey) && !string.IsNullOrWhiteSpace(SecretKey);
}
