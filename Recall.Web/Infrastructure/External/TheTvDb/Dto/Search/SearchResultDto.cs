using System.Text.Json.Serialization;

namespace Recall.Web.Infrastructure.External.TheTvDb.Dto.Search;

public sealed class SearchResultDto
{
    [JsonPropertyName("tvdb_id")]
    public int TvdbId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("overview")]
    public string? Overview { get; init; }

    /// <summary>Overviews by TheTVDB language code ("eng", "deu"); <see cref="Overview"/> is the one in <see cref="PrimaryLanguage"/>.</summary>
    [JsonPropertyName("overviews")]
    public Dictionary<string, string>? Overviews { get; init; }

    /// <summary>TheTVDB language code of the title's original language, e.g. "deu".</summary>
    [JsonPropertyName("primary_language")]
    public string? PrimaryLanguage { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; init; }

    [JsonPropertyName("year")]
    public string? Year { get; init; }
}