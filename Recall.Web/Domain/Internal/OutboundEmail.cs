namespace Recall.Web.Domain.Internal;

/// <summary>
/// Domain model for a queued outbound email. Repositories accept and return this
/// type; callers never touch <c>EmailEntity</c> directly.
/// </summary>
public sealed class OutboundEmail
{
    public Guid Id { get; init; }

    /// <summary>Lower value is sent first; <c>0</c> is "normal".</summary>
    public int Priority { get; init; }

    /// <summary>One or more recipients, comma-separated.</summary>
    public string ToAddress { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;

    /// <summary>Plain-text body — always set; the fallback part of the message.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Optional HTML body; when set the message is sent multipart/alternative.</summary>
    public string? HtmlBody { get; init; }

    public int SendAttempts { get; init; }

    public DateTime? SentUtc { get; init; }

    public DateTime CreatedUtc { get; init; }

    public DateTime UpdatedUtc { get; init; }
}
