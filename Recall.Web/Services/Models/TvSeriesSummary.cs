namespace Recall.Web.Services.Models;

public sealed record TvSeriesSummary(
    int TvdbId,
    string Name,
    string? Overview,
    string? ImageUrl,
    string? Year);