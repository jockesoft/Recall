namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>
/// A single-use, time-limited passwordless sign-in token. The raw token is only
/// ever emailed to the user — this row stores its SHA-256 hash, so a leak of the
/// table can't be turned into a login.
/// </summary>
public sealed class LoginTokenEntity : IHasAuditTimestamps
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public AppUserEntity User { get; set; } = null!;

    /// <summary>Base64 SHA-256 of the raw token that was emailed.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// Base64 SHA-256 of the 6-digit sign-in code emailed with the link,
    /// salted with <see cref="Id"/> (<c>SignInCode.Hash</c>). The link and the
    /// code are two ways to redeem this one row, so using either consumes both.
    /// Null on rows from before codes existed.
    /// </summary>
    public string? CodeHash { get; set; }

    /// <summary>Wrong codes entered against this token. At <c>SignInCode.MaxWrongAttempts</c> the token is consumed.</summary>
    public int CodeAttempts { get; set; }

    public DateTime ExpiresUtc { get; set; }

    /// <summary>Set the moment the link is redeemed; <c>null</c> while still usable.</summary>
    public DateTime? ConsumedUtc { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
