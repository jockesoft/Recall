namespace Recall.Web.Services.Digest;

/// <summary>
/// When the weekly digest is due. The job runs every hour and asks this; the
/// answer is the week (named by its scheduled send date) whose digest should be
/// going out right now, or nothing.
/// </summary>
public static class DigestSchedule
{
    /// <summary>
    /// The most recent scheduled moment (<see cref="DigestOptions.DayOfWeek"/>
    /// at <see cref="DigestOptions.HourUtc"/>, UTC) that has passed, as long as
    /// it is no more than <see cref="DigestOptions.CatchUpHours"/> ago. Within
    /// that window every hourly run works on the same week, so a restart at the
    /// scheduled hour, or more recipients than one run takes, only delays the
    /// digest. After it, nothing is due until next week.
    /// </summary>
    public static DateOnly? DuePeriod(DateTimeOffset nowUtc, DigestOptions options)
    {
        var now = nowUtc.UtcDateTime;
        var daysSince = ((int)now.DayOfWeek - (int)options.DayOfWeek + 7) % 7;
        var scheduled = now.Date.AddDays(-daysSince).AddHours(options.HourUtc);

        // The scheduled day is today, but its hour has not come yet: the last one was a week ago.
        if (scheduled > now)
            scheduled = scheduled.AddDays(-7);

        return now - scheduled <= TimeSpan.FromHours(Math.Max(1, options.CatchUpHours))
            ? DateOnly.FromDateTime(scheduled)
            : null;
    }
}
