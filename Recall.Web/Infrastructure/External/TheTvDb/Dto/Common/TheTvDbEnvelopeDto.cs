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
}