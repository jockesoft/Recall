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
}
