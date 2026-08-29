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
    /// When true, requesting a new link consumes any earlier unused links for
    /// that account, so only the most recent email works.
    /// </summary>
    public bool InvalidatePreviousTokens { get; set; } = true;
}
