namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>
/// Persistent snapshot of a <c>SeriesAggregate</c> for one series + language.
/// Acts as a durable fallback tier below Redis. First reads insert a row; the
/// background refresh job overwrites rows whose <c>KeepUpdated</c> flag is set.
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

    /// <summary>
    /// How many regular episodes (not specials, not movie-flagged entries) had
    /// aired when <see cref="Payload"/> was written, and how many of those had
    /// a still. Denormalized so the episode refresh can skip, in SQL, the
    /// episodes of a series that rarely has stills (<c>StillRecheck</c>).
    /// </summary>
    public int AiredEpisodeCount { get; set; }

    /// <inheritdoc cref="AiredEpisodeCount"/>
    public int AiredStillCount { get; set; }

    public DateTime RetrievedUtc { get; set; }

    /// <summary>
    /// Which version of the DTO-to-aggregate mapping wrote <see cref="Payload"/>
    /// (<c>SeriesDataDtoMappings.AggregateVersion</c> at the time). A row below
    /// the current version lacks a field newer rows have; the refresh job takes
    /// those first. 0: written before the column existed, without genres.
    /// </summary>
    public int MappingVersion { get; set; }
}
