using Recall.Web.Domain.TheTvDb;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Services.Digest;

/// <summary>"A new season of X is out": a regular season whose first episode aired in the past week and is not watched yet.</summary>
public sealed record DigestPremiere(int SeriesId, string SeriesName, int SeasonNumber, int EpisodeId, DateOnly Aired);

/// <summary>
/// One line of episodes of one season of one series: a single episode (with its
/// name) or a run ("E05–E07").
/// </summary>
/// <param name="LinkEpisodeId">The first episode of the run: where the line links to.</param>
/// <param name="EpisodeName">The episode's name when the line is a single episode and the name is known.</param>
/// <param name="FirstAired">The air date of the first episode of the run.</param>
public sealed record DigestEpisodeLine(
    int SeriesId,
    string SeriesName,
    int SeasonNumber,
    int? EpisodeFrom,
    int? EpisodeTo,
    int EpisodeCount,
    int LinkEpisodeId,
    string? EpisodeName,
    DateOnly FirstAired)
{
    /// <summary>"S02 · E05" or "S02 · E05–E07".</summary>
    public string Code
    {
        get
        {
            var season = $"S{SeasonNumber:D2}";
            if (EpisodeFrom is not { } from)
                return season;

            return EpisodeTo is { } to && to != from ? $"{season} · E{from:D2}–E{to:D2}" : $"{season} · E{from:D2}";
        }
    }
}

/// <summary>One of the digest's sections: at most <see cref="DigestBuilder.MaxPerSection"/> lines, and how many were left out.</summary>
public sealed record DigestSection<T>(IReadOnlyList<T> Items, int MoreCount)
{
    public static DigestSection<T> Empty { get; } = new([], 0);

    public int TotalCount => Items.Count + MoreCount;
}

/// <summary>What one user's weekly digest says. <see cref="IsEmpty"/> means there is nothing to say and no email is sent.</summary>
public sealed record DigestContent(
    DigestSection<DigestPremiere> NewSeasons,
    DigestSection<DigestEpisodeLine> ReadyToWatch,
    DigestSection<DigestEpisodeLine> ComingUp)
{
    public bool IsEmpty => NewSeasons.TotalCount == 0 && ReadyToWatch.TotalCount == 0 && ComingUp.TotalCount == 0;

    /// <summary>Episodes in the "ready to watch" lines that are shown.</summary>
    public int ReadyEpisodeCount => ReadyToWatch.Items.Sum(line => line.EpisodeCount);

    /// <summary>Episodes in the "coming up" lines that are shown.</summary>
    public int ComingEpisodeCount => ComingUp.Items.Sum(line => line.EpisodeCount);
}

/// <summary>
/// Decides what goes into a weekly digest. Pure: everything it needs is passed
/// in, including today's date, so it can be tested without a clock, a database
/// or TheTVDB. It uses the rules the pages use:
/// <list type="bullet">
/// <item>Specials (season 0) are never mentioned (<see cref="WatchableEpisode.IsSpecial"/>).</item>
/// <item>Which series are dormant, and which a season premiere brings back, is
/// <see cref="ContinueWatchingOrder.Arrange"/>'s call. A dormant series appears
/// only under "new season".</item>
/// </list>
/// Three sections, each about the 7 days before or after the moment it is
/// built for, judged by release moment (<see cref="EpisodeRelease"/>):
/// <list type="number">
/// <item><b>New seasons</b>: any tracked series whose regular season's first
/// episode aired in the past week and is not watched.</item>
/// <item><b>Ready to watch</b>: for series in the main continue-watching list
/// (and not already under new seasons), regular episodes that aired in the past
/// week and are not watched, in the continue-watching order.</item>
/// <item><b>Coming up</b>: regular episodes airing in the coming week, for
/// every tracked series that is not dormant, soonest first.</item>
/// </list>
/// </summary>
public static class DigestBuilder
{
    /// <summary>How far back "the past week" and how far ahead "the coming week" reach, in days.</summary>
    public const int WindowDays = 7;

    /// <summary>Lines shown per section; the rest become "and N more".</summary>
    public const int MaxPerSection = 10;

    /// <param name="tracked">The cached aggregates of the series the user tracks (a series that is not cached is simply absent).</param>
    /// <param name="watchedEpisodeIds">Every episode id the user has watched in those series.</param>
    /// <param name="lastWatchedUtc">Latest watch per series (<c>GetLastWatchedUtcBySeriesAsync</c>).</param>
    /// <param name="addedUtc">When each series was added to the library (<see cref="ContinueWatchingOrder.AddedUtc"/>).</param>
    public static DigestContent Build(
        IEnumerable<SeriesAggregate> tracked,
        IReadOnlySet<int> watchedEpisodeIds,
        IReadOnlyDictionary<int, DateTime> lastWatchedUtc,
        IReadOnlyDictionary<int, DateTime> addedUtc,
        DateTime nowUtc,
        LibraryOptions libraryOptions)
    {
        // Windows of seven days either side of the send, by release moment
        // (EpisodeRelease): a US evening episode is "coming up" until it airs.
        var today = DateOnly.FromDateTime(nowUtc);
        var weekAgo = nowUtc.AddDays(-WindowDays);
        var weekAhead = nowUtc.AddDays(WindowDays);

        var series = tracked
            .Select(aggregate => new
            {
                Aggregate = aggregate,
                Progress = WatchProgressCalculator.Build(aggregate.TvdbId, aggregate.ToWatchableEpisodes(), watchedEpisodeIds, nowUtc)
            })
            .ToList();

        // The queue, split the way the Dashboard and the Library split it:
        // series with a next episode to watch. A series never started with
        // nothing aired yet is under Watching in the Library too, but it has no
        // next episode and is never dormant, so its premiere stays in "coming up".
        var queue = series.Where(s => !s.Progress.IsUpToDate).ToList();
        var recentPremieres = queue
            .Where(s => ContinueWatchingOrder.HasRecentPremiere(s.Progress.OrderedEpisodes, nowUtc, libraryOptions.PremiereReturnDays))
            .Select(s => s.Aggregate.TvdbId)
            .ToHashSet();

        var arranged = ContinueWatchingOrder.Arrange(
            queue, s => s.Aggregate.TvdbId, s => s.Aggregate.Name, lastWatchedUtc, addedUtc, recentPremieres, today, libraryOptions);

        var dormantIds = arranged.Dormant.Select(s => s.Aggregate.TvdbId).ToHashSet();

        // 1. New seasons: every tracked series, dormant ones included.
        var newSeasons = series
            .Select(s => new { s.Aggregate, Premiere = UnwatchedPremiere(s.Progress.OrderedEpisodes, watchedEpisodeIds, weekAgo, nowUtc) })
            .Where(s => s.Premiere is not null)
            .Select(s => new DigestPremiere(
                s.Aggregate.TvdbId, s.Aggregate.Name, s.Premiere!.SeasonNumber!.Value, s.Premiere.Id, s.Premiere.Aired!.Value))
            .OrderByDescending(p => p.Aired)
            .ThenBy(p => p.SeriesName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.SeriesId)
            .ToList();

        var premiereIds = newSeasons.Select(p => p.SeriesId).ToHashSet();

        // 2. Ready to watch: the main list, in its own order.
        var ready = arranged.Active
            .Where(s => !premiereIds.Contains(s.Aggregate.TvdbId))
            .SelectMany(s => Lines(
                s.Aggregate,
                s.Progress.OrderedEpisodes.Where(e => IsRegular(e)
                                                      && ReleasedBetween(e, weekAgo, nowUtc)
                                                      && !watchedEpisodeIds.Contains(e.Id))))
            .ToList();

        // 3. Coming up: everything tracked that is not dormant. An episode the
        // user has already marked (allowed from its air date, in any zone)
        // is not news, even before its release moment.
        var coming = series
            .Where(s => !dormantIds.Contains(s.Aggregate.TvdbId))
            .SelectMany(s => Lines(
                s.Aggregate,
                s.Progress.OrderedEpisodes.Where(e => IsRegular(e)
                                                      && e.Release is { } release
                                                      && release.Utc > nowUtc && release.Utc <= weekAhead
                                                      && !watchedEpisodeIds.Contains(e.Id))))
            .OrderBy(line => line.FirstAired)
            .ThenBy(line => line.SeriesName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(line => line.SeriesId)
            .ThenBy(line => line.SeasonNumber)
            .ToList();

        return new DigestContent(Cap(newSeasons), Cap(ready), Cap(coming));
    }

    private static bool IsRegular(WatchableEpisode episode) => episode.SeasonNumber is > 0;

    /// <summary>Released after <paramref name="from"/> and by <paramref name="to"/> (both moments in UTC).</summary>
    private static bool ReleasedBetween(WatchableEpisode episode, DateTime from, DateTime to) =>
        episode.Release is { } release && release.Utc > from && release.Utc <= to;

    /// <summary>The first episode of a regular season, if it aired within the window and is not watched; the newest such season.</summary>
    private static WatchableEpisode? UnwatchedPremiere(
        IEnumerable<WatchableEpisode> episodes,
        IReadOnlySet<int> watchedEpisodeIds,
        DateTime from,
        DateTime to) =>
        episodes
            .Where(e => IsRegular(e) && e.EpisodeNumber.HasValue)
            .GroupBy(e => e.SeasonNumber)
            .Select(season => season.OrderBy(e => e.EpisodeNumber).ThenBy(e => e.Id).First())
            .Where(first => ReleasedBetween(first, from, to) && !watchedEpisodeIds.Contains(first.Id))
            .OrderByDescending(first => first.SeasonNumber)
            .FirstOrDefault();

    /// <summary>One line per season of the series that has episodes in the set.</summary>
    private static IEnumerable<DigestEpisodeLine> Lines(SeriesAggregate aggregate, IEnumerable<WatchableEpisode> episodes) =>
        episodes
            .GroupBy(e => e.SeasonNumber!.Value)
            .OrderBy(season => season.Key)
            .Select(season =>
            {
                var ordered = season.OrderBy(e => e.EpisodeNumber ?? int.MaxValue).ThenBy(e => e.Id).ToList();
                var numbers = ordered.Where(e => e.EpisodeNumber.HasValue).Select(e => e.EpisodeNumber!.Value).ToList();
                var first = ordered[0];

                return new DigestEpisodeLine(
                    aggregate.TvdbId,
                    aggregate.Name,
                    season.Key,
                    numbers.Count > 0 ? numbers.Min() : null,
                    numbers.Count > 0 ? numbers.Max() : null,
                    ordered.Count,
                    first.Id,
                    ordered.Count == 1 ? KnownName(first.Name) : null,
                    ordered.Min(e => e.Aired!.Value));
            });

    /// <summary>An episode name worth printing: not blank, and not TheTVDB's "TBA" placeholder.</summary>
    private static string? KnownName(string? name) =>
        string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), "TBA", StringComparison.OrdinalIgnoreCase)
            ? null
            : name.Trim();

    private static DigestSection<T> Cap<T>(IReadOnlyList<T> items) =>
        new(items.Take(MaxPerSection).ToList(), Math.Max(0, items.Count - MaxPerSection));
}
