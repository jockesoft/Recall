using System.Text.Json.Serialization;

namespace Recall.Web.Infrastructure.External.TheTvDb.Dto.Auth;

public sealed class LoginRequestDto
{
    [JsonPropertyName("apikey")]
    public string ApiKey { get; init; } = string.Empty;

    [JsonPropertyName("pin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Pin { get; init; }
}