namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>
/// Persistent snapshot of a <c>MovieAggregate</c> for one movie + language.
/// Acts as a durable fallback tier below Redis. First reads insert a row; the
/// background refresh job overwrites rows whose <c>KeepUpdated</c> flag is set.
/// </summary>
public sealed class CachedMovieAggregateEntity
{
    public int TvdbId { get; set; }
    public string Language { get; set; } = "eng";

    public string Name { get; set; } = string.Empty;
    public string? StatusName { get; set; }
    public bool? KeepUpdated { get; set; }

    /// <summary>Serialized <c>MovieAggregate</c> (jsonb).</summary>
    public string Payload { get; set; } = string.Empty;

    public DateTime RetrievedUtc { get; set; }
}
