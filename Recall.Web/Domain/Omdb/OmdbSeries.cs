using System.Text.Json.Serialization;

namespace Recall.Web.Domain.Omdb;

/// <summary>A series record from OMDb. Stored in <c>cached_series_omdb</c>.</summary>
public sealed record OmdbSeries : OmdbTitle
{
    [JsonPropertyName("totalSeasons")] public string? TotalSeasons { get; init; }
}
