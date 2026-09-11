namespace Recall.Web.Domain.TheTvDb;

public sealed record MovieAggregate
{
    public int TvdbId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Slug { get; init; }
    public string? Overview { get; init; }
    public string? ImageUrl { get; init; }

    public DateOnly? ReleaseDate { get; init; }
    public string? Year { get; init; }
    public int? RuntimeMinutes { get; init; }

    public string? OriginalCountry { get; init; }
    public string? OriginalLanguage { get; init; }
    public double? Score { get; init; }

    public decimal? Budget { get; init; }
    public decimal? BoxOffice { get; init; }

    public MovieStatus? Status { get; init; }

    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<string> Studios { get; init; } = [];
    public IReadOnlyList<Character> Characters { get; init; } = [];
    public IReadOnlyList<MovieRemoteId> RemoteIds { get; init; } = [];
}

public sealed class MovieStatus
{
    public int? Id { get; init; }
    public string? Name { get; init; }
    public bool? KeepUpdated { get; init; }
    public string? RecordType { get; init; }
}

public sealed class MovieRemoteId
{
    public string? Id { get; init; }
    public int? Type { get; init; }
    public string? SourceName { get; init; }
}
