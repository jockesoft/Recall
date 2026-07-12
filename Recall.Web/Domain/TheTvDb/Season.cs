namespace Recall.Web.Domain.TheTvDb;

public record Season()
{
    public int? Id { get; init; }
    public string? Image { get; init; }
    public int? ImageType { get; init; }
    public string? LastUpdated { get; init; }
    public string? Name { get; init; }
    public List<string>? NameTranslations { get; init; }
    public int? Number { get; init; }
    public List<string>? OverviewTranslations { get; init; }
//    public CompaniesDto? Companies { get; init; }
    public int? SeriesId { get; init; }
    public SeasonType? Type { get; init; }
    public string? Year { get; init; }
}