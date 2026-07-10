namespace Recall.Web.Domain.Internal;

// Domain
public sealed class UserItem
{
    public Guid Id { get; init; }
    public string UserId { get; init; } = default!;   // Identity user id
    public string Username { get; init; } = default!;
    public string Email { get; init; } = default!;
    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }
}