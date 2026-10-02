namespace Recall.Web.Services.WatchTracking;

/// <summary>
/// A user's watch position within a single series: the ordered episode list,
/// which episodes are watched, and the next episode they should watch (if any).
/// The next episode and every count are about regular episodes only; specials
/// (season 0) are in <see cref="OrderedEpisodes"/> but never count.
/// </summary>
public sealed class SeriesWatchProgress
{
    public required int SeriesTvdbId { get; init; }

    /// <summary>Non-movie episodes in watch order: numbered seasons, then specials.</summary>
    public required IReadOnlyList<WatchableEpisode> OrderedEpisodes { get; init; }

    public required IReadOnlySet<int> WatchedEpisodeIds { get; init; }

    /// <summary>
    /// Earliest released regular episode the user has not marked watched. Never
    /// a special. Null when every regular episode that has aired is watched.
    /// </summary>
    public WatchableEpisode? NextUnwatchedEpisode { get; init; }

    /// <summary>
    /// Progress through the season of <see cref="NextUnwatchedEpisode"/> ("6 of
    /// 16 · S05" on a Library card). Null when the user is up to date, or the
    /// next episode has no season number.
    /// </summary>
    public SeasonWatchProgress? CurrentSeason { get; init; }

    /// <summary>Count of regular episodes that have aired as of the build date.</summary>
    public required int ReleasedCount { get; init; }

    /// <summary>Count of aired regular episodes the user has marked watched.</summary>
    public required int WatchedReleasedCount { get; init; }

    /// <summary>Count of released regular episodes not yet marked watched.</summary>
    public int UnwatchedReleasedCount => ReleasedCount - WatchedReleasedCount;

    public bool HasEpisodes => OrderedEpisodes.Count > 0;

    /// <summary>
    /// True when no aired regular episode is unwatched, even if specials are.
    /// Also true for a series with nothing to watch yet, so on its own it does
    /// not mean the user has seen anything: see <see cref="HasStarted"/>.
    /// </summary>
    public bool IsUpToDate => NextUnwatchedEpisode is null;

    /// <summary>
    /// True when the user has watched at least one regular episode. Specials
    /// do not start a series, and neither does adding it to the library. (Any
    /// regular episode counts, including one with no air date, which can be
    /// marked watched but is never in <see cref="ReleasedCount"/>.)
    /// </summary>
    public bool HasStarted => OrderedEpisodes.Any(e => !e.IsSpecial && WatchedEpisodeIds.Contains(e.Id));
}

/// <summary>Aired episodes of one season, and how many of them the user has watched.</summary>
public sealed record SeasonWatchProgress(int SeasonNumber, int WatchedCount, int ReleasedCount)
{
    /// <summary>"6 of 16 · S05".</summary>
    public string Label => $"{WatchedCount} of {ReleasedCount} · S{SeasonNumber:D2}";
}
