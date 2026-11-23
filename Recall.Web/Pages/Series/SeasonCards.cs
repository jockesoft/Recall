using System.Globalization;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Display;
using Recall.Web.Pages.Shared;
using Recall.Web.Services;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Pages.Series;

/// <summary>What an episode row's round control is.</summary>
public enum EpisodeMark
{
    /// <summary>Filled green with a check; pressing it unmarks.</summary>
    Watched,

    /// <summary>
    /// May be marked (<see cref="AirDate.MayBeMarked"/>) and is not: a plain
    /// ring, or the amber "ready" one for the episode the primary button
    /// marks (<see cref="EpisodeRow.IsReady"/>).
    /// </summary>
    Unwatched,

    /// <summary>A dashed circle with a clock, not a button.</summary>
    NotAired
}

/// <summary>One episode in a season card.</summary>
/// <param name="Code">"E8": the season is the card's.</param>
/// <param name="AiredText">"Fri, Oct 2", for a released episode.</param>
/// <param name="Upcoming">The release time of an episode not released yet, in place of the runtime.</param>
/// <param name="IsReady">
/// The next episode, when the hero's primary button marks it: its control is
/// the amber "ready" one. Every other unwatched episode's is a plain ring.
/// </param>
public sealed record EpisodeRow(
    int Id,
    string Code,
    string Name,
    string? Overview,
    string? AiredText,
    ReleaseTimeModel? Upcoming,
    int? RuntimeMinutes,
    EpisodeMark Mark,
    bool IsReady = false);

/// <summary>
/// One season of Series Details as a card (<c>details</c>/<c>summary</c>):
/// "Season 17 · 2026 · 9 episodes", a thin bar and "7/8" (watched of
/// released), and the episode rows.
/// </summary>
public sealed record SeasonCard(
    int Number,
    int? Year,
    int ReleasedCount,
    int WatchedReleasedCount,
    int WatchedCount,
    bool HasMarkable,
    bool IsOpen,
    bool IsEarlier,
    IReadOnlyList<EpisodeRow> Episodes)
{
    /// <summary>Season 0. Its card is dashed and says it is not counted in progress.</summary>
    public bool IsSpecials => Number == 0;

    /// <summary>"Season 17", or "Specials".</summary>
    public string Title => IsSpecials ? "Specials" : $"Season {Number.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>"Season 17 · 2026 · 9 episodes".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { Title };
            if (Year is { } year)
                parts.Add(year.ToString(CultureInfo.InvariantCulture));
            parts.Add(Episodes.Count == 1 ? "1 episode" : $"{Episodes.Count.ToString(CultureInfo.InvariantCulture)} episodes");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Every released episode of the season is watched (and something is released).</summary>
    public bool AllWatched => ReleasedCount > 0 && WatchedReleasedCount >= ReleasedCount;

    /// <summary>The bar's length, a whole percent.</summary>
    public int Percent => ReleasedCount <= 0 ? 0 : (int)Math.Floor(100.0 * WatchedReleasedCount / ReleasedCount);

    /// <summary>The id of the card, for links and the "Season" redirects: "season-17".</summary>
    public string Anchor => SeasonCards.Anchor(Number);
}

/// <summary>What <c>_SeasonCard</c> draws: the card, and what its forms need.</summary>
public sealed record SeasonCardView(SeasonCard Card, int SeriesId, bool SignedIn);

/// <summary>
/// Builds Series Details' season cards. Pure: everything comes in, so the
/// rules are tested without a page.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Numbered seasons in order, then the specials, last.</item>
/// <item>Open: the season of the next episode, else the latest numbered season
/// (<see cref="WatchProgressCalculator.DefaultSeason"/>), and the season in
/// the address (<c>?season=</c>, where a mark sends the user back to).</item>
/// <item>Numbered seasons older than the <see cref="RecentSeasons"/> most
/// recent are grouped behind "Show N earlier seasons"; the group is open when
/// one of its seasons is.</item>
/// <item>Only the episode the hero's primary button marks gets the amber
/// "ready" control; the page passes it as <c>readyEpisodeId</c>.</item>
/// <item>The counts are released regular episodes (the specials never count,
/// and an episode TheTVDB flags as a movie is left out of them, as in all
/// progress).</item>
/// </list>
/// </remarks>
public static class SeasonCards
{
    /// <summary>How many of the latest numbered seasons are always shown.</summary>
    public const int RecentSeasons = 3;

    public static string Anchor(int season) => $"season-{season.ToString(CultureInfo.InvariantCulture)}";

    public static string EpisodeAnchor(int episodeId) => $"episode-{episodeId.ToString(CultureInfo.InvariantCulture)}";

    public static IReadOnlyList<SeasonCard> Build(
        SeriesAggregate series,
        SeriesWatchProgress progress,
        int? askedSeason,
        DateTime now,
        DateOnly today,
        int? readyEpisodeId = null)
    {
        var watched = progress.WatchedEpisodeIds;
        var watchable = progress.OrderedEpisodes.ToDictionary(e => e.Id);

        var numbers = series.Episodes
            .Select(e => e.SeasonNumber)
            .Concat(series.Seasons.Select(s => s.Number))
            .Where(n => n.HasValue)
            .Select(n => n!.Value)
            .Distinct()
            .OrderBy(n => EpisodeOrderingExtensions.SeasonRank(n))
            .ThenBy(n => n)
            .ToList();

        var regular = numbers.Where(n => n != 0).ToList();
        var earlier = regular.Take(Math.Max(regular.Count - RecentSeasons, 0)).ToHashSet();

        var open = new HashSet<int>();
        if (WatchProgressCalculator.DefaultSeason(progress) is { } defaultSeason)
            open.Add(defaultSeason);
        if (askedSeason is { } asked)
            open.Add(asked);
        // A series with seasons but no episode to judge by still opens one.
        if (open.Count == 0 && numbers.Count > 0)
            open.Add(regular.Count > 0 ? regular[^1] : numbers[0]);

        return numbers.Select(number =>
        {
            var episodes = series.Episodes
                .Where(e => e.SeasonNumber == number)
                .OrderBy(e => e.EpisodeNumber ?? int.MaxValue)
                .ThenBy(e => e.Id)
                .ToList();

            // Counted: what progress counts (not a movie, released by now).
            var released = episodes
                .Where(e => watchable.TryGetValue(e.Id, out var w) && w.IsReleasedBy(now))
                .ToList();

            var rows = episodes.Select(e => Row(series, e, watched, now, today, readyEpisodeId)).ToList();

            return new SeasonCard(
                number,
                Year(episodes, series.Seasons.FirstOrDefault(s => s.Number == number)),
                ReleasedCount: released.Count,
                WatchedReleasedCount: released.Count(e => watched.Contains(e.Id)),
                WatchedCount: episodes.Count(e => watched.Contains(e.Id)),
                HasMarkable: rows.Any(r => r.Mark == EpisodeMark.Unwatched),
                IsOpen: open.Contains(number),
                IsEarlier: earlier.Contains(number),
                rows);
        }).ToList();
    }

    private static EpisodeRow Row(
        SeriesAggregate series, EpisodeSummary episode, IReadOnlySet<int> watched, DateTime now, DateOnly today, int? readyEpisodeId)
    {
        var release = EpisodeRelease.MomentUtc(episode.Aired, series.AirsTime, series.OriginalCountry);
        var isReleased = release is not null && release.IsReleasedBy(now);

        var mark = watched.Contains(episode.Id)
            ? EpisodeMark.Watched
            : AirDate.MayBeMarked(episode.Aired, now) ? EpisodeMark.Unwatched : EpisodeMark.NotAired;

        return new EpisodeRow(
            episode.Id,
            $"E{episode.EpisodeNumber?.ToString(CultureInfo.InvariantCulture) ?? "?"}",
            episode.Name,
            string.IsNullOrWhiteSpace(episode.Overview) ? null : episode.Overview,
            isReleased && episode.Aired is { } aired ? DisplayDate.Format(aired, today) : null,
            !isReleased && release is not null ? new ReleaseTimeModel(release, today) : null,
            episode.RuntimeMinutes is > 0 ? episode.RuntimeMinutes : null,
            mark,
            IsReady: mark == EpisodeMark.Unwatched && episode.Id == readyEpisodeId);
    }

    /// <summary>The year the season started: its first air date, else TheTVDB's season year.</summary>
    private static int? Year(IReadOnlyList<EpisodeSummary> episodes, SeasonSummary? season)
    {
        var first = episodes.Where(e => e.Aired.HasValue).Select(e => e.Aired!.Value).DefaultIfEmpty().Min();
        if (first != default)
            return first.Year;

        return int.TryParse(season?.Year, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ? year : null;
    }

    /// <summary>
    /// How long released regular episodes take: each episode's own runtime,
    /// else the series' average. Null when neither is known for any of them.
    /// </summary>
    public static int? Minutes(SeriesAggregate series, IEnumerable<int> episodeIds)
    {
        var byId = series.Episodes.ToDictionary(e => e.Id);
        int? total = null;

        foreach (var id in episodeIds)
        {
            var minutes = byId.TryGetValue(id, out var episode) && episode.RuntimeMinutes is > 0
                ? episode.RuntimeMinutes
                : series.AverageRuntimeMinutes is > 0 ? series.AverageRuntimeMinutes : null;

            if (minutes is { } value)
                total = (total ?? 0) + value;
        }

        return total;
    }
}
