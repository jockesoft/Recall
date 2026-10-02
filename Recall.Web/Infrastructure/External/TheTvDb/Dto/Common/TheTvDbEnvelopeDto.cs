using System.Text.Json.Serialization;

namespace Recall.Web.Infrastructure.External.TheTvDb.Dto.Common;

public sealed class TheTvDbEnvelopeDto<T>
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("data")]
    public T? Data { get; init; }

    /// <summary>Paging links of a paged response; TheTVDB puts them beside <c>data</c>, not inside it.</summary>
    [JsonPropertyName("links")]
    public Series.PagingLinksDto? Links { get; init; }
}