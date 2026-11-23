namespace Recall.Web.Domain.TheTvDb;

public sealed record MovieAggregate
{
    public int TvdbId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Slug { get; init; }
    public string? Overview { get; init; }
    public string? ImageUrl { get; init; }

    /// <summary>
    /// The movie's 16:9 background art, sized for the hero: TheTVDB's
    /// thumbnail of its best-scored background (artwork type 15). Null when
    /// the movie has none, and on a row cached before this was mapped
    /// (2026-11-23) until it is next refreshed.
    /// </summary>
    public string? BackgroundUrl { get; init; }

    /// <summary>The first release's date (TheTVDB's <c>first_release</c>).</summary>
    public DateOnly? ReleaseDate { get; init; }

    /// <summary>
    /// What the first release was, when TheTVDB says ("Theatrical", "Digital",
    /// "Premiere"); usually it does not.
    /// </summary>
    public string? ReleaseDetail { get; init; }
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
