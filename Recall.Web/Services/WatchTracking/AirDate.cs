namespace Recall.Web.Services.WatchTracking;

/// <summary>
/// The one definition of "today" and "now" for air-date comparisons, in UTC
/// everywhere (pages, services and background jobs alike) rather than the
/// server's local date, which differs between a developer machine and
/// production. Whether an episode is released is <see cref="EpisodeRelease"/>'s
/// call (a moment, not a date); whether it may be marked watched is
/// <see cref="MayBeMarked"/>.
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

    /// <summary>The current moment in UTC: what "released" is judged against (<see cref="EpisodeRelease"/>).</summary>
    public static DateTime Now(TimeProvider timeProvider) => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Whether an episode may be marked watched: once its air date has begun
    /// anywhere on Earth (UTC+14), so someone who has just watched it, wherever
    /// they are, can mark it straight away, even before it counts as
    /// "released". Deliberately looser than release, which drives
    /// notifications and progress. An unknown air date may always be marked:
    /// TheTVDB is missing dates for plenty of episodes that aired long ago.
    /// </summary>
    public static bool MayBeMarked(DateOnly? aired, DateTime nowUtc) =>
        aired is not { } date || date <= DateOnly.FromDateTime(nowUtc.AddHours(14));
}
