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
    /// Coarse "1y 9mo 26d 20h" breakdown. A month is 30 days and a year is
    /// 12 months (360 days) so the parts stay internally consistent; leading
    /// zero units are dropped, hours are always shown.
    /// </summary>
    public string Formatted
    {
        get
        {
            if (TotalMinutes <= 0)
                return "0h";

            long totalHours = TotalMinutes / 60;
            long hours = totalHours % 24;
            long totalDays = totalHours / 24;
            long days = totalDays % 30;
            long totalMonths = totalDays / 30;
            long months = totalMonths % 12;
            long years = totalMonths / 12;

            var parts = new List<string>(4);
            if (years > 0) parts.Add($"{years}y");
            if (months > 0 || parts.Count > 0) parts.Add($"{months}mo");
            if (days > 0 || parts.Count > 0) parts.Add($"{days}d");
            parts.Add($"{hours}h");

            return string.Join(' ', parts);
        }
    }
}
