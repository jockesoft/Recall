using System.Text.Json.Serialization;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Common;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;

namespace Recall.Web.Infrastructure.External.TheTvDb.Dto.Movies;

/// <summary>
/// Shape of TheTVDB's <c>/movies/{id}/extended</c> response. Reuses the common
/// sub-objects already defined for series (<see cref="StatusDto"/>, <see cref="GenreDto"/>,
/// <see cref="RemoteIdDto"/>, <see cref="CompaniesDto"/>) since TheTVDB shares these
/// shapes between series and movies.
/// </summary>
public sealed class MovieDataDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("slug")]
    public string? Slug { get; init; }

    [JsonPropertyName("image")]
    public string? Image { get; init; }

    [JsonPropertyName("score")]
    public double? Score { get; init; }

    [JsonPropertyName("runtime")]
    public int? Runtime { get; init; }

    [JsonPropertyName("year")]
    public string? Year { get; init; }

    [JsonPropertyName("status")]
    public StatusDto? Status { get; init; }

    [JsonPropertyName("genres")]
    public List<GenreDto>? Genres { get; init; }

    [JsonPropertyName("remoteIds")]
    public List<RemoteIdDto>? RemoteIds { get; init; }

    [JsonPropertyName("characters")]
    public List<CharacterDataDto>? Characters { get; init; }

    [JsonPropertyName("companies")]
    public CompaniesDto? Companies { get; init; }

    [JsonPropertyName("budget")]
    public string? Budget { get; init; }

    [JsonPropertyName("boxOffice")]
    public string? BoxOffice { get; init; }

    [JsonPropertyName("originalCountry")]
    public string? OriginalCountry { get; init; }

    [JsonPropertyName("originalLanguage")]
    public string? OriginalLanguage { get; init; }

    [JsonPropertyName("first_release")]
    public MovieReleaseDto? FirstRelease { get; init; }
}

public sealed class MovieReleaseDto
{
    [JsonPropertyName("country")]
    public string? Country { get; init; }

    [JsonPropertyName("date")]
    public string? Date { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}
