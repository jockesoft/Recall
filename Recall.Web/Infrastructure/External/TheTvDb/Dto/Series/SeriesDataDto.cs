using System.Text.Json.Serialization;

namespace Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;

public sealed class SeriesDataDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("slug")]
    public string? Slug { get; init; }

    [JsonPropertyName("overview")]
    public string? Overview { get; init; }

    [JsonPropertyName("image")]
    public string? Image { get; init; }

    [JsonPropertyName("firstAired")]
    public string? FirstAired { get; init; }

    [JsonPropertyName("score")]
    public double? Score { get; init; }
}