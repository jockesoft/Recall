using System.Text.Json.Serialization;

namespace Recall.Web.Infrastructure.External.TheTvDb.Dto.Search;

/// <summary>
/// One match from <c>GET /search/remoteid/{remoteId}</c>. TheTVDB populates
/// whichever of these applies to the matched entity (series, movie, people, ...);
/// Recall only cares about <see cref="Series"/> and <see cref="Movie"/>.
/// </summary>
public sealed class SearchByRemoteIdResultDto
{
    [JsonPropertyName("series")]
    public RemoteIdBaseRecordDto? Series { get; init; }

    [JsonPropertyName("movie")]
    public RemoteIdBaseRecordDto? Movie { get; init; }
}

public sealed class RemoteIdBaseRecordDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
