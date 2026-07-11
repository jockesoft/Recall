using System.Text.Json.Serialization;

namespace Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;

public sealed class SeasonDto
{
    [JsonPropertyName("id")]
    public int? Id { get; init; }

    [JsonPropertyName("image")]
    public string? Image { get; init; }

    [JsonPropertyName("imageType")]
    public int? ImageType { get; init; }

    [JsonPropertyName("lastUpdated")]
    public string? LastUpdated { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("nameTranslations")]
    public List<string>? NameTranslations { get; init; }

    [JsonPropertyName("number")]
    public int? Number { get; init; }

    [JsonPropertyName("overviewTranslations")]
    public List<string>? OverviewTranslations { get; init; }

    [JsonPropertyName("companies")]
    public CompaniesDto? Companies { get; init; }

    [JsonPropertyName("seriesId")]
    public int? SeriesId { get; init; }

    [JsonPropertyName("type")]
    public SeasonTypeDto? Type { get; init; }

    [JsonPropertyName("year")]
    public string? Year { get; init; }
}

public record EpisodeDto
{
    [JsonPropertyName("absoluteNumber")]
    public int? AbsoluteNumber { get; init; }

    [JsonPropertyName("aired")]
    public string? Aired { get; init; }

    [JsonPropertyName("airsAfterSeason")]
    public int? AirsAfterSeason { get; init; }

    [JsonPropertyName("airsBeforeEpisode")]
    public int? AirsBeforeEpisode { get; init; }

    [JsonPropertyName("airsBeforeSeason")]
    public int? AirsBeforeSeason { get; init; }

    [JsonPropertyName("finaleType")]
    public string? FinaleType { get; init; }

    [JsonPropertyName("id")]
    public int? Id { get; init; }

    [JsonPropertyName("image")]
    public string? Image { get; init; }

    [JsonPropertyName("imageType")]
    public int? ImageType { get; init; }

    [JsonPropertyName("isMovie")]
    public int? IsMovie { get; init; }

    [JsonPropertyName("lastUpdated")]
    public string? LastUpdated { get; init; }

    [JsonPropertyName("linkedMovie")]
    public int? LinkedMovie { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("nameTranslations")]
    public List<string>? NameTranslations { get; init; }

    [JsonPropertyName("number")]
    public int? Number { get; init; }

    [JsonPropertyName("overview")]
    public string? Overview { get; init; }

    [JsonPropertyName("overviewTranslations")]
    public List<string>? OverviewTranslations { get; init; }

    [JsonPropertyName("runtime")]
    public int? Runtime { get; init; }

    [JsonPropertyName("seasonNumber")]
    public int? SeasonNumber { get; init; }

    [JsonPropertyName("seasons")]
    public List<SeasonDto>? Seasons { get; init; }

    [JsonPropertyName("seriesId")]
    public int? SeriesId { get; init; }

    [JsonPropertyName("seasonName")]
    public string? SeasonName { get; init; }

    [JsonPropertyName("year")]
    public string? Year { get; init; }
}