namespace Recall.Web.Domain.TheTvDb;

public record SeasonType()
{
    public string? AlternateName { get; init; }
    public int? Id { get; init; }
    public string? Name { get; init; }
    public string? Type { get; init; }
}