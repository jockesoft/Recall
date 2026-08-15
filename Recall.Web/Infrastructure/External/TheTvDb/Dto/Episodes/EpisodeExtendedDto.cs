using System.Text.Json.Serialization;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;

namespace Recall.Web.Infrastructure.External.TheTvDb.Dto.Episodes;

public sealed class ContentRatingDto
{
    [JsonPropertyName("id")]
    public int? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("country")]
    public string? Country { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("contentType")]
    public string? ContentType { get; init; }

    [JsonPropertyName("order")]
    public int? Order { get; init; }

    [JsonPropertyName("fullName")]
    public string? FullName { get; init; }
}

public sealed class AwardNomineeDto
{
    [JsonPropertyName("id")]
    public int? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("isWinner")]
    public bool? IsWinner { get; init; }
}

public sealed class AwardDto
{
    [JsonPropertyName("id")]
    public int? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("year")]
    public string? Year { get; init; }

    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("isWinner")]
    public bool? IsWinner { get; init; }

    [JsonPropertyName("nominees")]
    public List<AwardNomineeDto>? Nominees { get; init; }
}

/// <summary>
/// Response from GET /episodes/{id}/extended — includes all base episode fields
/// plus score, content ratings, and awards.
/// </summary>
public sealed record EpisodeExtendedDto : EpisodeDto
{
    [JsonPropertyName("score")]
    public double? Score { get; init; }

    [JsonPropertyName("contentRatings")]
    public List<ContentRatingDto>? ContentRatings { get; init; }

    [JsonPropertyName("awards")]
    public List<AwardDto>? Awards { get; init; }
}

