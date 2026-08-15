using System.Text.Json.Serialization;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Common;

namespace Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;

public sealed class SeriesDataDto
{
    [JsonPropertyName("aliases")]
    public List<AliasDto>? Aliases { get; init; }

    [JsonPropertyName("averageRuntime")]
    public int? AverageRuntime { get; init; }
    
    [JsonPropertyName("characters")]
    public List<CharacterDataDto>? Characters { get; init; }

    [JsonPropertyName("companies")]
    public List<CompanyDto>? Companies { get; init; }

    [JsonPropertyName("country")]
    public string? Country { get; init; }

    [JsonPropertyName("defaultSeasonType")]
    public int? DefaultSeasonType { get; init; }

    [JsonPropertyName("episodes")]
    public List<EpisodeDto>? Episodes { get; init; }

    [JsonPropertyName("firstAired")]
    public string? FirstAired { get; init; }

    [JsonPropertyName("genres")]
    public List<GenreDto>? Genres { get; init; }

    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("image")]
    public string? Image { get; init; }

    [JsonPropertyName("isOrderRandomized")]
    public bool? IsOrderRandomized { get; init; }

    [JsonPropertyName("lastAired")]
    public string? LastAired { get; init; }

    [JsonPropertyName("lastUpdated")]
    public string? LastUpdated { get; init; }

    [JsonPropertyName("lists")]
    public List<SeriesListDto>? Lists { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("nameTranslations")]
    public List<string>? NameTranslations { get; init; }

    [JsonPropertyName("nextAired")]
    public string? NextAired { get; init; }

    [JsonPropertyName("originalCountry")]
    public string? OriginalCountry { get; init; }

    [JsonPropertyName("originalLanguage")]
    public string? OriginalLanguage { get; init; }

    [JsonPropertyName("overview")]
    public string? Overview { get; init; }

    [JsonPropertyName("overviewTranslations")]
    public List<string>? OverviewTranslations { get; init; }

    [JsonPropertyName("remoteIds")]
    public List<RemoteIdDto>? RemoteIds { get; init; }

    [JsonPropertyName("score")]
    public double? Score { get; init; }

    [JsonPropertyName("slug")]
    public string? Slug { get; init; }

    [JsonPropertyName("status")]
    public StatusDto? Status { get; init; }

    [JsonPropertyName("year")]
    public string? Year { get; init; }
    
    [JsonPropertyName("seasons")]
    public List<SeasonDto>? Seasons { get; init; }
}