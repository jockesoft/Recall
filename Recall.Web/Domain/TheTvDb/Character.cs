namespace Recall.Web.Domain.TheTvDb;

public sealed class Character
{
    public int Id { get; init; }
    public string? Name { get; init; }
    public string? Image { get; init; }
    public bool IsFeatured { get; init; }

    public int? PeopleId { get; init; }
    public string? PersonName { get; init; }
    public string? PersonImageUrl { get; init; }
    public string? PeopleType { get; init; }

    public int? Type { get; init; }
    public int? Sort { get; init; }
    public string? Url { get; init; }

    public int? EpisodeId { get; init; }
    public RelatedItem? Episode { get; init; }

    public int? MovieId { get; init; }
    public RelatedItem? Movie { get; init; }

    public int? SeriesId { get; init; }
    public RelatedItem? Series { get; init; }

    public List<CharacterAlias> Aliases { get; init; } = new();
    public List<string> NameTranslations { get; init; } = new();
    public List<string> OverviewTranslations { get; init; } = new();
    public List<CharacterTagOption> TagOptions { get; init; } = new();
}