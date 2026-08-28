namespace Recall.Web.Services.WatchTracking;

/// <summary>
/// A user's watch position within a single series: the ordered episode list,
/// which episodes are watched, and the next episode they should watch (if any).
/// </summary>
public sealed class SeriesWatchProgress
{
    public required int SeriesTvdbId { get; init; }

    /// <summary>Non-movie episodes in season/episode order.</summary>
    public required IReadOnlyList<WatchableEpisode> OrderedEpisodes { get; init; }

    public required IReadOnlySet<int> WatchedEpisodeIds { get; init; }

    /// <summary>
    /// Earliest released episode (season/episode order) the user has not marked
    /// watched. Null when the user is caught up on everything that has aired.
    /// </summary>
    public WatchableEpisode? NextUnwatchedEpisode { get; init; }

    /// <summary>Count of released episodes not yet marked watched.</summary>
    public int UnwatchedReleasedCount { get; init; }

    public bool HasEpisodes => OrderedEpisodes.Count > 0;

    public bool IsUpToDate => NextUnwatchedEpisode is null;
}
