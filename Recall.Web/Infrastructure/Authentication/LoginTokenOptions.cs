namespace Recall.Web.Infrastructure.Authentication;

/// <summary>
/// Bound from the <c>Login</c> configuration section — tunes the passwordless
/// (magic-link) sign-in flow.
/// </summary>
public sealed class LoginTokenOptions
{
    public const string SectionName = "Login";

    /// <summary>How long an emailed sign-in link stays valid.</summary>
    public int TokenLifetimeMinutes { get; set; } = 15;

    /// <summary>
    /// Minimum gap between sign-in emails to the same address. A repeat request
    /// inside this window is accepted but sends nothing, so the form can't be
    /// used to flood an inbox. Set to 0 to disable.
    /// </summary>
    public int ResendCooldownSeconds { get; set; } = 120;

    /// <summary>
    /// When true, requesting a new link consumes any earlier unused links for
    /// that account, so only the most recent email works.
    /// </summary>
    public bool InvalidatePreviousTokens { get; set; } = true;

    /// <summary>
    /// Email addresses permitted to request a sign-in link (and, on first use,
    /// have an account provisioned). Matched case-insensitively after trimming.
    /// When empty, any address is allowed.
    /// </summary>
    public string[] AllowedEmails { get; set; } = [];
}
