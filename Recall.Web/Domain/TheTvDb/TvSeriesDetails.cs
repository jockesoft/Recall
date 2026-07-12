namespace Recall.Web.Domain.TheTvDb;

public sealed record TvSeriesDetails(
    int TvdbId,
    string Name,
    string? Slug,
    string? Overview,
    string? ImageUrl,
    string? FirstAired,
    double? Score);