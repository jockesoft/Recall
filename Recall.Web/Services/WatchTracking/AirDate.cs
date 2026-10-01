namespace Recall.Web.Services.WatchTracking;

/// <summary>
/// The one definition of "today" for air-date comparisons. TheTVDB air dates
/// carry no time zone, so they're compared against the UTC date everywhere —
/// pages, services and background jobs alike — rather than the server's local
/// date, which differs between a developer machine and production.
///
/// The clock is the injected <see cref="TimeProvider"/>, never
/// <c>DateTime.Today</c> or <c>DateTime.UtcNow</c>: take one in the
/// constructor and call <see cref="Today"/>, so tests can pin the date.
/// </summary>
public static class AirDate
{
    /// <summary>The current date in UTC, whatever time zone the clock (or the machine) is set to.</summary>
    public static DateOnly Today(TimeProvider timeProvider) =>
        DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

    /// <summary>
    /// True only for a known air date that is after <paramref name="today"/>.
    /// An unknown date is not treated as unaired: TheTVDB is missing dates for
    /// plenty of episodes that aired long ago.
    /// </summary>
    public static bool IsInFuture(DateOnly? aired, DateOnly today) => aired is { } date && date > today;
}
