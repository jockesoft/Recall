namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>
/// Persistent OMDb snapshot for one series, keyed by its TheTVDB id. Same shape
/// as <see cref="CachedSeriesExtendedEntity"/>, with two additions: the IMDb id
/// used for the lookup, and a nullable <see cref="Payload"/> so a series we
/// checked but could not enrich (no IMDb id, or OMDb had nothing) still gets a
/// dated row and isn't re-checked until it goes stale.
/// </summary>
public sealed class CachedSeriesOmdbEntity
{
    public int TvdbId { get; set; }

    /// <summary>IMDb id used for the OMDb lookup; null when the series had none.</summary>
    public string? ImdbId { get; set; }

    public string? Name { get; set; }

    /// <summary>Serialized <c>OmdbSeries</c> (jsonb); null when there was nothing to store.</summary>
    public string? Payload { get; set; }

    public DateTime RetrievedUtc { get; set; }
}
