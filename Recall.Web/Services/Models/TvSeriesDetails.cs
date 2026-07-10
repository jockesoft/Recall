namespace Recall.Web.Services.Models;

public sealed record TvSeriesDetails(
    int TvdbId,
    string Name,
    string? Slug,
    string? Overview,
    string? ImageUrl,
    string? FirstAired,
    double? Score);