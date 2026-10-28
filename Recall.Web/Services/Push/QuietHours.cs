namespace Recall.Web.Services.Push;

/// <summary>
/// Quiet hours: 22:00 to 08:00 on the device's own clock. A push due then
/// waits until 08:00 there. Pure: the time and the zone are passed in.
/// </summary>
public static class QuietHours
{
    public static readonly TimeOnly Start = new(22, 0);
    public static readonly TimeOnly End = new(8, 0);

    /// <summary>Whether <paramref name="utc"/> falls between 22:00 and 08:00 in <paramref name="zone"/>.</summary>
    public static bool IsQuiet(DateTime utc, TimeZoneInfo zone)
    {
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Utc(utc), zone));
        return local >= Start || local < End;
    }

    /// <summary>
    /// The next 08:00 in <paramref name="zone"/> after <paramref name="utc"/>,
    /// in UTC: the same morning after midnight, the next one before it. Daylight
    /// saving is the zone's: an 08:00 that does not exist that day (no zone has
    /// one today, but the rule is cheap) moves an hour on.
    /// </summary>
    public static DateTime NextRelease(DateTime utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(Utc(utc), zone);
        var day = DateOnly.FromDateTime(local);
        if (TimeOnly.FromDateTime(local) >= End)
            day = day.AddDays(1);

        var release = day.ToDateTime(End, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(release))
            release = release.AddHours(1);

        return TimeZoneInfo.ConvertTimeToUtc(release, zone);
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
