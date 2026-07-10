using System.Text.Json.Serialization;

namespace Recall.Web.Infrastructure.External.TheTvDb.Dto.Episodes;

public sealed class EpisodeTranslationDataDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("overview")]
    public string? Overview { get; init; }

    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("isPrimary")]
    public bool? IsPrimary { get; init; }

    [JsonPropertyName("aliases")]
    public List<string>? Aliases { get; init; }

    [JsonPropertyName("tagline")]
    public string? Tagline { get; init; }
}