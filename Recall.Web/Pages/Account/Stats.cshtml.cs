using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Infrastructure.Display;
using Recall.Web.Services;
using Recall.Web.Services.Stats;

namespace Recall.Web.Pages.Account;

/// <summary>
/// "Your stats": totals, a month-by-month chart, the most watched series and
/// genres, and the spread of the user's ratings. Everything on it comes from
/// <see cref="IStatsService"/>, which reads the database and the metadata
/// caches only: opening this page never costs a TheTVDB or OMDb request.
/// </summary>
[Authorize]
public sealed class StatsModel(
    ICurrentUserService currentUser,
    IStatsService statsService,
    ILogger<StatsModel> logger) : PageModel
{
    public UserStats Stats { get; private set; } = UserStats.Empty;

    /// <summary>The stats could not be computed; the page says so instead of showing zeroes.</summary>
    public bool LoadFailed { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
            return;

        try
        {
            Stats = await statsService.GetAsync(userId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to compute stats for user {UserId}.", userId);
            LoadFailed = true;
            Response.StatusCode = StatusCodes.Status500InternalServerError;
        }
    }

    /// <summary>
    /// The sentence under the heading. The tiles already give the hours and the
    /// counts, so this adds the one thing they do not: the total as days
    /// ("That's about 34 days of watching."). Below two days the hours on the
    /// tile say it all, and there is no sentence.
    /// </summary>
    public string? Lead
    {
        get
        {
            var days = Stats.Totals.Minutes / (60.0 * 24);

            return days < 2
                ? null
                : $"That's about {StatsFormat.Number((int)Math.Round(days, MidpointRounding.AwayFromZero))} days of watching.";
        }
    }

    /// <summary>
    /// A month's bar as a percentage of the tallest. By watch time; if no month
    /// has any (nothing watched has a known length), by how much was watched.
    /// </summary>
    public int MonthBarPercent(StatsMonth month) =>
        Stats.LargestMonthMinutes > 0
            ? StatsFormat.Percent(month.Minutes, Stats.LargestMonthMinutes)
            : StatsFormat.Percent(month.Episodes + month.Movies, Stats.Months.Max(m => m.Episodes + m.Movies));

    /// <summary>What a screen reader hears for one column: "October 2026: 12 episodes, 1 movie, 9 h 40 min".</summary>
    public static string MonthText(StatsMonth month)
    {
        var name = DisplayDate.Month(month.Month);

        if (month.Episodes == 0 && month.Movies == 0)
            return $"{name}: nothing";

        var parts = new List<string>(3);
        if (month.Episodes > 0) parts.Add(StatsFormat.Count(month.Episodes, "episode"));
        if (month.Movies > 0) parts.Add(StatsFormat.Count(month.Movies, "movie"));
        if (month.Minutes > 0) parts.Add(StatsFormat.Duration(month.Minutes));

        return $"{name}: {string.Join(", ", parts)}";
    }

    /// <summary>"815 episodes and 2 movies": what the chart leaves out.</summary>
    public string UndatedText => CountsText(Stats.Undated.Episodes, Stats.Undated.Movies);

    /// <summary>
    /// Why the chart is empty, for a user who has watched things: everything
    /// was marked in bulk or imported, or nothing was marked in the last year.
    /// </summary>
    public string NoChartText => Stats.Undated.Any
        ? "Episodes and movies you mark as you watch them show up here, month by month. "
          + $"The {UndatedText} you marked several at once or imported are counted in the totals above, "
          + "but their date is when they were marked, not when you watched them."
        : "Episodes and movies you mark as you watch them show up here, month by month. "
          + "Nothing has been marked in the last twelve months.";

    /// <summary>
    /// One sentence about what was counted without a length, or null when
    /// everything has one: "Counted without a length: 3 titles whose details
    /// aren't loaded yet and 2 episodes with no known length."
    /// </summary>
    public string? GapsText
    {
        get
        {
            var gaps = Stats.Gaps;
            if (!gaps.Any)
                return null;

            var parts = new List<string>(3);
            if (gaps.TitlesNotCached > 0)
                parts.Add($"{StatsFormat.Count(gaps.TitlesNotCached, "title")} whose details aren't loaded yet");
            if (gaps.EpisodesWithoutRuntime > 0)
                parts.Add($"{StatsFormat.Count(gaps.EpisodesWithoutRuntime, "episode")} with no known length");
            if (gaps.MoviesWithoutRuntime > 0)
                parts.Add($"{StatsFormat.Count(gaps.MoviesWithoutRuntime, "movie")} with no known length");

            var list = parts.Count switch
            {
                1 => parts[0],
                2 => $"{parts[0]} and {parts[1]}",
                _ => $"{parts[0]}, {parts[1]} and {parts[2]}"
            };

            return $"Counted without a length: {list}.";
        }
    }

    private static string CountsText(int episodes, int movies)
    {
        var parts = new List<string>(2);
        if (episodes > 0) parts.Add(StatsFormat.Count(episodes, "episode"));
        if (movies > 0) parts.Add(StatsFormat.Count(movies, "movie"));
        return string.Join(" and ", parts);
    }
}
