namespace Recall.Web.Services.WatchTracking;

/// <summary>
/// Total time a user has spent on what they have watched: episodes (their own
/// runtime, else the series' average) and movies. Computed by
/// <c>StatsBuilder</c>; this type only puts the total into words.
/// </summary>
public sealed record WatchTimeSummary(int TotalMinutes, int EpisodeCount, int MovieCount = 0)
{
    public static WatchTimeSummary Empty { get; } = new(0, 0);

    /// <summary>"815 episodes and 3 movies", "1 episode", "2 movies"; a kind at zero is left out.</summary>
    public string Across
    {
        get
        {
            var parts = new List<string>(2);
            if (EpisodeCount > 0) parts.Add($"{EpisodeCount} episode{(EpisodeCount == 1 ? "" : "s")}");
            if (MovieCount > 0) parts.Add($"{MovieCount} movie{(MovieCount == 1 ? "" : "s")}");
            return string.Join(" and ", parts);
        }
    }

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
