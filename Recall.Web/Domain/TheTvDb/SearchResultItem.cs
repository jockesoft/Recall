namespace Recall.Web.Domain.TheTvDb;

/// <param name="Overview">The English overview when TheTVDB has one, otherwise the one in the title's own language.</param>
/// <param name="OverviewLanguage">
/// Language tag for the <c>lang</c> attribute ("de", "ja") when <paramref name="Overview"/>
/// is not English; null when it is English or its language is unknown.
/// </param>
public sealed record SearchResultItem(
    int TvdbId,
    string Name,
    string? Overview,
    string? ImageUrl,
    string? Year,
    SearchResultType Type,
    string? OverviewLanguage = null);
