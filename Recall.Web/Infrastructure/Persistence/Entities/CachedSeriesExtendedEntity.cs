namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>
/// Persistent snapshot of an extended <c>Series</c> (domain) for one series.
/// Durable fallback tier below Redis; insert-only until refresh is built.
/// </summary>
public sealed class CachedSeriesExtendedEntity
{
    public int TvdbId { get; set; }

    public string? Name { get; set; }

    /// <summary>Serialized <c>Series</c> (jsonb).</summary>
    public string Payload { get; set; } = string.Empty;

    public DateTime RetrievedUtc { get; set; }
}
