using System.Globalization;

namespace Recall.Web.Infrastructure.Display;

/// <summary>
/// The one place dates are turned into text for people. A date in the current
/// year reads "Sat, Sep 4"; any other year reads "Sep 4, 2026"
/// (<see cref="Short(DateOnly, DateOnly)"/> drops the weekday where space is
/// tight). Relative time
/// ("3h ago") is for notifications only. Machine-readable output (sitemap,
/// JSON-LD, form values) keeps its own ISO formatting and does not come here.
/// </summary>
public static class DisplayDate
{
    private static readonly CultureInfo English = CultureInfo.InvariantCulture;

    /// <summary>"Sat, Sep 4" in the current year, "Sep 4, 2026" otherwise.</summary>
    public static string Format(DateOnly date, DateOnly today) =>
        date.Year == today.Year
            ? date.ToString("ddd, MMM d", English)
            : date.ToString("MMM d, yyyy", English);

    /// <inheritdoc cref="Format(DateOnly, DateOnly)"/>
    public static string Format(DateTime date, DateOnly today) =>
        Format(DateOnly.FromDateTime(date), today);

    /// <summary>
    /// The same date without the weekday, for a tight spot such as a poster
    /// card's meta line: "Sep 4" in the current year, "Sep 4, 2025" otherwise.
    /// </summary>
    public static string Short(DateOnly date, DateOnly today) =>
        date.Year == today.Year
            ? date.ToString("MMM d", English)
            : date.ToString("MMM d, yyyy", English);

    /// <inheritdoc cref="Short(DateOnly, DateOnly)"/>
    public static string Short(DateTime date, DateOnly today) =>
        Short(DateOnly.FromDateTime(date), today);

    /// <summary>
    /// Formats a date that arrives as text, as TheTVDB sends them
    /// ("2008-01-20", "2024-12-23 18:07:56"). Text that is not a date is
    /// returned unchanged, and nothing at all for a blank value.
    /// </summary>
    public static string? Format(string? value, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateTime.TryParse(value, English, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? Format(parsed, today)
            : value;
    }

    /// <summary>A calendar month in full: "October 2026".</summary>
    public static string Month(DateOnly month) => month.ToString("MMMM yyyy", English);

    /// <summary>A calendar month as a chart label: "Oct".</summary>
    public static string MonthShort(DateOnly month) => month.ToString("MMM", English);

    /// <summary>A time of day: "8:00 PM".</summary>
    public static string Time(TimeOnly time) => time.ToString("h:mm tt", English);

    /// <summary>"just now", "5m ago", "3h ago", "2d ago", "4w ago". Notifications only.</summary>
    public static string Relative(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";
        if (age < TimeSpan.FromDays(7)) return $"{(int)age.TotalDays}d ago";
        return $"{(int)(age.TotalDays / 7)}w ago";
    }
}
