namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>
/// Persistent snapshot of an extended <c>Episode</c> (domain) for one episode.
/// Durable fallback tier below Redis; insert-only until refresh is built.
/// </summary>
public sealed class CachedEpisodeExtendedEntity
{
    public int EpisodeTvdbId { get; set; }

    public int? SeriesTvdbId { get; set; }
    public string? Name { get; set; }

    /// <summary>Serialized <c>Episode</c> (jsonb).</summary>
    public string Payload { get; set; } = string.Empty;

    public DateTime RetrievedUtc { get; set; }
}
