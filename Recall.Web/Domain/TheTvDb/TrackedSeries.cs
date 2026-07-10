namespace Recall.Web.Domain.TheTvDb;

/// <summary>
/// Domain model for a series tracked locally in Recall.
/// </summary>
public sealed class TrackedSeries
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    public int TvdbId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Overview { get; init; }
    public string? ImageUrl { get; init; }
    public DateOnly? FirstAired { get; init; }

    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }

    /// <summary>
    /// Optimistic concurrency token.
    /// </summary>
    public uint Version { get; init; }
}