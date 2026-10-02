namespace Recall.Web.Services;

public static class EpisodeOrderingExtensions
{
    /// <summary>
    /// TheTVDB's onscreen episode order: season, then episode number, both with
    /// unknown (null) numbers sorting last, then a final id tie-break to keep the
    /// order stable and deterministic. Used everywhere episodes need a
    /// consistent order — the watch-progress list, the raw TheTVDB episode
    /// loader, and notification digests — so the tie-break rule only has to be
    /// changed in one place if it ever needs to.
    /// </summary>
    public static IOrderedEnumerable<T> OrderBySeasonAndEpisode<T>(
        this IEnumerable<T> source,
        Func<T, int?> seasonNumber,
        Func<T, int?> episodeNumber,
        Func<T, int> tieBreakId) =>
        source
            .OrderBy(e => seasonNumber(e) ?? int.MaxValue)
            .ThenBy(e => episodeNumber(e) ?? int.MaxValue)
            .ThenBy(tieBreakId);

    /// <summary>
    /// The order a series is watched in: the numbered seasons first, then the
    /// specials (season 0), then anything without a season. Within that it is
    /// <see cref="OrderBySeasonAndEpisode{T}"/>. Specials are extras: they are
    /// listed after everything else and are never "earlier" than a regular
    /// episode.
    /// </summary>
    public static IOrderedEnumerable<T> OrderByWatchOrder<T>(
        this IEnumerable<T> source,
        Func<T, int?> seasonNumber,
        Func<T, int?> episodeNumber,
        Func<T, int> tieBreakId) =>
        source
            .OrderBy(e => SeasonRank(seasonNumber(e)))
            .ThenBy(e => seasonNumber(e) ?? int.MaxValue)
            .ThenBy(e => episodeNumber(e) ?? int.MaxValue)
            .ThenBy(tieBreakId);

    /// <summary>Sort key for a season in watch order: numbered, then specials, then unknown.</summary>
    public static int SeasonRank(int? seasonNumber) => seasonNumber switch
    {
        null => 2,
        0 => 1,
        _ => 0
    };
}
