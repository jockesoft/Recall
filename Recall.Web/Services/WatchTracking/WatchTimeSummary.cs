namespace Recall.Web.Services.WatchTracking;

/// <summary>
/// Total time a user has spent on watched episodes, summed from episode
/// runtimes (falling back to a series' average runtime when an episode has none).
/// </summary>
public sealed record WatchTimeSummary(int TotalMinutes, int EpisodeCount)
{
    public static WatchTimeSummary Empty { get; } = new(0, 0);

    public bool HasData => TotalMinutes > 0;

    /// <summary>
    /// The total in words, as its two largest units: "1 year, 9 months",
    /// "1 month, 3 days", "5 days, 7 hours", "45 minutes". A month is 30 days
    /// and a year is 12 months (360 days), so the parts stay consistent with
    /// each other; a unit at zero is left out ("2 days", not "2 days, 0 hours").
    /// </summary>
    public string Readable
    {
        get
        {
            if (TotalMinutes <= 0)
                return "0 minutes";

            long minutes = TotalMinutes % 60;
            long totalHours = TotalMinutes / 60;
            long hours = totalHours % 24;
            long totalDays = totalHours / 24;
            long days = totalDays % 30;
            long totalMonths = totalDays / 30;
            long months = totalMonths % 12;
            long years = totalMonths / 12;

            (long Value, string Unit)[] units =
            [
                (years, "year"), (months, "month"), (days, "day"), (hours, "hour"), (minutes, "minute")
            ];

            // The largest unit that is not zero, and the one right below it.
            var first = Array.FindIndex(units, u => u.Value > 0);
            var parts = units
                .Skip(first)
                .Take(2)
                .Where(u => u.Value > 0)
                .Select(u => $"{u.Value} {u.Unit}{(u.Value == 1 ? "" : "s")}");

            return string.Join(", ", parts);
        }
    }
}
