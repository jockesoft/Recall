namespace Recall.Web.Domain.TheTvDb;

public sealed record Episode
{
    public int? AbsoluteNumber { get; init; }
    public string? Aired { get; init; }
    public int? AirsAfterSeason { get; init; }
    public int? AirsBeforeEpisode { get; init; }
    public int? AirsBeforeSeason { get; init; }
    public string? FinaleType { get; init; }
    public int? Id { get; init; }
    public string? Image { get; init; }
    public int? ImageType { get; init; }
    public bool IsMovie { get; init; }                 // mapped from int? (0/1)
    public string? LastUpdated { get; init; }
    public int? LinkedMovie { get; init; }
    public string? Name { get; init; }
    public IReadOnlyList<string> NameTranslations { get; init; } = [];
    public int? Number { get; init; }
    public string? Overview { get; init; }
    public IReadOnlyList<string> OverviewTranslations { get; init; } = [];
    public int? Runtime { get; init; }
    public int? SeasonNumber { get; init; }
    public IReadOnlyList<Season> Seasons { get; init; } = [];
    public int? SeriesId { get; init; }
    public string? SeasonName { get; init; }
    public string? Year { get; init; }

    /// <summary>Community score from TheTVDB (scale varies; typically 0–10).</summary>
    public double? Score { get; init; }

    public IReadOnlyList<EpisodeAward> Awards { get; init; } = [];
    public IReadOnlyList<EpisodeContentRating> ContentRatings { get; init; } = [];
}

public sealed record EpisodeAward
{
    public int? Id { get; init; }
    public string? Name { get; init; }
    public string? Year { get; init; }
    public string? Category { get; init; }
    public bool IsWinner { get; init; }
}

public sealed record EpisodeContentRating
{
    public string? Name { get; init; }
    public string? Country { get; init; }
    public string? Description { get; init; }
    public string? FullName { get; init; }
}