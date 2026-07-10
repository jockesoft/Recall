namespace Recall.Web.Domain.TheTvDb;

public sealed class SeriesAggregate
{
    public int TvdbId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Slug { get; init; }
    public string? Overview { get; init; }
    public string? ImageUrl { get; init; }

    public DateOnly? FirstAired { get; init; }
    public DateOnly? LastAired { get; init; }
    public DateOnly? NextAired { get; init; }

    public string? OriginalCountry { get; init; }
    public string? OriginalLanguage { get; init; }
    public double? Score { get; init; }
    public string? Year { get; init; }

    public int? AverageRuntimeMinutes { get; init; }

    public SeriesStatus? Status { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
    public IReadOnlyList<SeasonSummary> Seasons { get; init; } = Array.Empty<SeasonSummary>();
    public IReadOnlyList<EpisodeSummary> Episodes { get; init; } = Array.Empty<EpisodeSummary>();
}

public sealed class SeriesStatus
{
    public int? Id { get; init; }
    public string? Name { get; init; }
    public bool? KeepUpdated { get; init; }
    public string? RecordType { get; init; }
}

public sealed class SeasonSummary
{
    public int Id { get; init; }
    public int? Number { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? ImageUrl { get; init; }
    public string? Year { get; init; }
    public string? TypeName { get; init; }

    public IReadOnlyList<string> Studios { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Networks { get; init; } = Array.Empty<string>();
}

public sealed class EpisodeSummary
{
    public int Id { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Overview { get; init; }
    public DateOnly? Aired { get; init; }
    public int? RuntimeMinutes { get; init; }
    public bool? IsMovie { get; init; }
    public string? FinaleType { get; init; }
}