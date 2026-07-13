namespace Recall.Web.Domain.TheTvDb;

public sealed record Series
{
    public IReadOnlyList<SeriesAlias> Aliases { get; init; } = [];
    public int? AverageRuntime { get; init; }
    public List<Character> Characters { get; init; } = [];
    public string? Country { get; init; }
    public int? DefaultSeasonType { get; init; }
    public IReadOnlyList<Episode> Episodes { get; init; } = [];
    public string? FirstAired { get; init; }
    public int Id { get; init; }
    public string? Image { get; init; }
    public bool? IsOrderRandomized { get; init; }
    public string? LastAired { get; init; }
    public string? LastUpdated { get; init; }
    public string? Name { get; init; }
    public IReadOnlyList<string> NameTranslations { get; init; } = [];
    public string? NextAired { get; init; }
    public string? OriginalCountry { get; init; }
    public string? OriginalLanguage { get; init; }
    public IReadOnlyList<string> OverviewTranslations { get; init; } = [];
    public double? Score { get; init; }
    public string? Slug { get; init; }
    public SeriesStatusInfo? Status { get; init; }
    public string? Year { get; init; }
    public IReadOnlyList<Season> Seasons { get; init; } = [];
}

public sealed record SeriesAlias
{
    public string? Language { get; init; }
    public string? Name { get; init; }
}

public sealed record SeriesStatusInfo
{
    public int? Id { get; init; }
    public bool? KeepUpdated { get; init; }
    public string? Name { get; init; }
    public string? RecordType { get; init; }
}

