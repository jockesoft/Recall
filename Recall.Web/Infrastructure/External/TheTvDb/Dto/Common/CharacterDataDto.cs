using System.Text.Json.Serialization;

namespace Recall.Web.Infrastructure.External.TheTvDb.Dto.Common;

public sealed class CharacterDataDto
{
    [JsonPropertyName("aliases")]
    public List<CharacterAliasDto>? Aliases { get; init; }

    [JsonPropertyName("episode")]
    public CharacterRelatedItemDto? Episode { get; init; }

    [JsonPropertyName("episodeId")]
    public int? EpisodeId { get; init; }

    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("image")]
    public string? Image { get; init; }

    [JsonPropertyName("isFeatured")]
    public bool? IsFeatured { get; init; }

    [JsonPropertyName("movieId")]
    public int? MovieId { get; init; }

    [JsonPropertyName("movie")]
    public CharacterRelatedItemDto? Movie { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("nameTranslations")]
    public List<string>? NameTranslations { get; init; }

    [JsonPropertyName("overviewTranslations")]
    public List<string>? OverviewTranslations { get; init; }

    [JsonPropertyName("peopleId")]
    public int? PeopleId { get; init; }

    [JsonPropertyName("personImgURL")]
    public string? PersonImgUrl { get; init; }

    [JsonPropertyName("peopleType")]
    public string? PeopleType { get; init; }

    [JsonPropertyName("seriesId")]
    public int? SeriesId { get; init; }

    [JsonPropertyName("series")]
    public CharacterRelatedItemDto? Series { get; init; }

    [JsonPropertyName("sort")]
    public int? Sort { get; init; }

    [JsonPropertyName("tagOptions")]
    public List<CharacterTagOptionDto>? TagOptions { get; init; }

    [JsonPropertyName("type")]
    public int? Type { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("personName")]
    public string? PersonName { get; init; }
}

public sealed class CharacterAliasDto
{
    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class CharacterRelatedItemDto
{
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("year")]
    public string? Year { get; init; }
}

public sealed class CharacterTagOptionDto
{
    [JsonPropertyName("helpText")]
    public string? HelpText { get; init; }

    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("tag")]
    public int? Tag { get; init; }

    [JsonPropertyName("tagName")]
    public string? TagName { get; init; }
}