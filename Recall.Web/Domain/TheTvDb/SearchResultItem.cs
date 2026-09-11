namespace Recall.Web.Domain.TheTvDb;

public sealed record SearchResultItem(
    int TvdbId,
    string Name,
    string? Overview,
    string? ImageUrl,
    string? Year,
    SearchResultType Type);
