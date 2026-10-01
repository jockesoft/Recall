namespace Recall.Web.Services.WatchTracking;

/// <summary>
/// The one definition of "today" for air-date comparisons. TheTVDB air dates
/// carry no time zone, so they're compared against the UTC date everywhere —
/// pages, services and background jobs alike — rather than the server's local
/// date, which differs between a developer machine and production.
/// </summary>
public static class AirDate
{
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>
    /// True only for a known air date that is still in the future. An unknown
    /// date is not treated as unaired: TheTVDB is missing dates for plenty of
    /// episodes that aired long ago.
    /// </summary>
    public static bool IsInFuture(DateOnly? aired) => aired is { } date && date > Today;
}
