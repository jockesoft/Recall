namespace Recall.Web.Domain.Internal;

/// <summary>
/// Domain model for a passwordless sign-in token. Repositories accept and return
/// this type; callers never touch <c>LoginTokenEntity</c> directly.
/// </summary>
public sealed class LoginToken
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    /// <summary>Base64 SHA-256 of the raw token that was emailed.</summary>
    public string TokenHash { get; init; } = string.Empty;

    public DateTime ExpiresUtc { get; init; }

    public DateTime? ConsumedUtc { get; init; }

    public DateTime CreatedUtc { get; init; }

    public DateTime UpdatedUtc { get; init; }
}
