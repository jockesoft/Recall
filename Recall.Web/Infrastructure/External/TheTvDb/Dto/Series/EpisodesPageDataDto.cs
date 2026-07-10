using System.Text.Json.Serialization;

namespace Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;

public sealed class EpisodesPageDataDto
{
    [JsonPropertyName("series")]
    public SeriesDataDto? Series { get; init; }

    [JsonPropertyName("episodes")]
    public List<EpisodeDto>? Episodes { get; init; }

    [JsonPropertyName("links")]
    public PagingLinksDto? Links { get; init; }
}

public sealed class PagingLinksDto
{
    [JsonPropertyName("prev")]
    public string? Prev { get; init; }

    [JsonPropertyName("self")]
    public string? Self { get; init; }

    [JsonPropertyName("next")]
    public string? Next { get; init; }

    [JsonPropertyName("total_items")]
    public int? TotalItems { get; init; }

    [JsonPropertyName("page_size")]
    public int? PageSize { get; init; }
}