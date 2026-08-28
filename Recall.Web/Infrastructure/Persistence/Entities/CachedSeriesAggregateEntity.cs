namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>
/// Persistent snapshot of a <c>SeriesAggregate</c> for one series + language.
/// Acts as a durable fallback tier below Redis so a series is fetched from
/// TheTVDB at most once. Refreshing is out of scope — rows are insert-only.
/// </summary>
public sealed class CachedSeriesAggregateEntity
{
    public int TvdbId { get; set; }
    public string Language { get; set; } = "eng";

    public string Name { get; set; } = string.Empty;
    public string? StatusName { get; set; }
    public bool? KeepUpdated { get; set; }

    /// <summary>Serialized <c>SeriesAggregate</c> (jsonb).</summary>
    public string Payload { get; set; } = string.Empty;

    public DateTime RetrievedUtc { get; set; }
}
