using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Services.Discover;

/// <summary>
/// What a title needs to be suggested at all, applied by the job before a list
/// is stored: a poster (the card is the poster), a score of at least
/// <see cref="DiscoverOptions.MinScore"/>, and a sane year (TheTVDB has titles
/// dated 9696 or 3013).
/// </summary>
public static class DiscoverQuality
{
    /// <summary>The earliest year taken as real.</summary>
    public const int FirstYear = 1900;

    public static bool Passes(DiscoverTitle title, DateOnly today, int minScore) =>
        !string.IsNullOrWhiteSpace(title.ImageUrl)
        && title.Score >= minScore
        && title.Year is { } year
        && year >= FirstYear
        && year <= today.Year + 1
        && (title.FirstAired is not { } aired || aired <= today.AddYears(2));
}
