namespace Recall.Web.Services.WatchTracking;

/// <summary>
/// A user's watch position within a single series: the ordered episode list,
/// which episodes are watched, and the next episode they should watch (if any).
/// </summary>
public sealed class SeriesWatchProgress
{
    public required int SeriesTvdbId { get; init; }

    /// <summary>Non-movie episodes in watch order: numbered seasons, then specials.</summary>
    public required IReadOnlyList<WatchableEpisode> OrderedEpisodes { get; init; }

    public required IReadOnlySet<int> WatchedEpisodeIds { get; init; }

    /// <summary>
    /// Earliest released episode (watch order) the user has not marked watched:
    /// a regular episode while any is left, a special only when specials are
    /// all that remain. Null when the user is caught up on everything that has aired.
    /// </summary>
    public WatchableEpisode? NextUnwatchedEpisode { get; init; }

    /// <summary>Count of episodes that have aired as of the build date.</summary>
    public required int ReleasedCount { get; init; }

    /// <summary>Count of aired episodes the user has marked watched.</summary>
    public required int WatchedReleasedCount { get; init; }

    /// <summary>Count of released episodes not yet marked watched.</summary>
    public int UnwatchedReleasedCount => ReleasedCount - WatchedReleasedCount;

    public bool HasEpisodes => OrderedEpisodes.Count > 0;

    public bool IsUpToDate => NextUnwatchedEpisode is null;
}
