namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>
/// Persistent OMDb snapshot for one episode, keyed by its TheTVDB id. Same shape
/// as <see cref="CachedSeriesOmdbEntity"/>, but populated lazily the first time a
/// visitor opens Episodes/Details rather than by a background job — there are far
/// more episodes than series, so eagerly enriching every one wouldn't be worth
/// the OMDb quota.
/// </summary>
public sealed class CachedEpisodeOmdbEntity
{
    public int TvdbId { get; set; }

    /// <summary>IMDb id used for the OMDb lookup; null when the episode had none.</summary>
    public string? ImdbId { get; set; }

    public string? Name { get; set; }

    /// <summary>Serialized <c>OmdbSeries</c> (jsonb); null when there was nothing to store.</summary>
    public string? Payload { get; set; }

    public DateTime RetrievedUtc { get; set; }
}
