namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>
/// Persistent snapshot of an extended <c>Episode</c> (domain) for one episode.
/// Durable fallback tier below Redis. The background refresh job overwrites
/// existing rows via upsert.
/// </summary>
public sealed class CachedEpisodeExtendedEntity
{
    public int EpisodeTvdbId { get; set; }

    public int? SeriesTvdbId { get; set; }
    public string? Name { get; set; }

    /// <summary>Denormalized from the payload's air date, for refresh-query filtering.</summary>
    public DateOnly? Aired { get; set; }

    /// <summary>
    /// Denormalized from whether the payload's <c>Image</c> is set. An aired
    /// episode without one is rechecked on a schedule that depends on how long
    /// ago it aired (<see cref="TvdbCache.StillRecheck"/>).
    /// </summary>
    public bool HasImage { get; set; }

    /// <summary>Serialized <c>Episode</c> (jsonb).</summary>
    public string Payload { get; set; } = string.Empty;

    public DateTime RetrievedUtc { get; set; }
}
