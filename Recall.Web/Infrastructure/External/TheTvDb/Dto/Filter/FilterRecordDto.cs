using System.Text.Json.Serialization;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;

namespace Recall.Web.Infrastructure.External.TheTvDb.Dto.Filter;

/// <summary>
/// One result of <c>series/filter</c> or <c>movies/filter</c>: TheTVDB's base
/// record, and only the fields Discover reads. A series record has the first
/// air date; a movie record has none (nor an overview, a country or genres),
/// so a movie's date is only its <see cref="Year"/>. Neither has genres or a
/// usable <c>nextAired</c> (always empty, checked 2026-11-25).
/// </summary>
public sealed class FilterRecordDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Absolute for series, a relative path for movies: always normalized when mapped.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    /// <summary>A string ("2026"), sometimes missing; sometimes far in the future (junk such as 9696).</summary>
    [JsonPropertyName("year")]
    public string? Year { get; init; }

    /// <summary>Series only, "2026-10-13", or empty.</summary>
    [JsonPropertyName("firstAired")]
    public string? FirstAired { get; init; }

    [JsonPropertyName("score")]
    public double? Score { get; init; }

    [JsonPropertyName("status")]
    public StatusDto? Status { get; init; }
}
