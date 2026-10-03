using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Infrastructure.Display;

/// <summary>
/// What the server writes inside a release time's <c>&lt;time&gt;</c> element:
/// TheTVDB's air date, plus the air time in the series' own zone when it is
/// known ("Fri, Oct 2 · 8:00 PM ET"). In the browser, <c>js/tvdb-local-time.js</c>
/// rewrites a timed one into the viewer's own date and time ("Sat, Oct 3 · 2:00 AM");
/// this text is what shows without script, and a fallback moment (no air
/// time known) only ever shows its date.
/// </summary>
public static class ReleaseText
{
    /// <summary>"Fri, Oct 2 · 8:00 PM ET", or "Fri, Oct 2" when no time is known.</summary>
    public static string DateAndTime(ReleaseMoment release, DateOnly today) =>
        release is { IsTimeKnown: true, LocalTime: { } time }
            ? $"{DisplayDate.Format(release.AirDate, today)} · {TimeOf(time, release.ZoneLabel)}"
            : DisplayDate.Format(release.AirDate, today);

    /// <summary>"8:00 PM ET" alone (under a "Today" or "Tomorrow" heading); null when no time is known.</summary>
    public static string? TimeOnly(ReleaseMoment release) =>
        release is { IsTimeKnown: true, LocalTime: { } time } ? TimeOf(time, release.ZoneLabel) : null;

    private static string TimeOf(System.TimeOnly time, string? zone) =>
        string.IsNullOrWhiteSpace(zone) ? DisplayDate.Time(time) : $"{DisplayDate.Time(time)} {zone}";
}
