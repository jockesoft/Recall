using System.Text.Json.Serialization;

namespace Recall.Web.Infrastructure.External.TheTvDb.Dto.Auth;

public sealed class LoginDataDto
{
    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;
}