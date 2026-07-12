namespace Recall.Web.Domain.TheTvDb;

public sealed record TvSeriesSummary(
    int TvdbId,
    string Name,
    string? Overview,
    string? ImageUrl,
    string? Year);