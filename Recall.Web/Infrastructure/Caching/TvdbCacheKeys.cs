namespace Recall.Web.Infrastructure.Caching;

/// <summary>
/// The Redis keys of TheTVDB's cached resources, without the instance prefix
/// (<c>tvdb:</c>, set in <c>AddRedisCache</c>). The version in a key names the
/// format of its value: bumping it means old entries are simply never read
/// again and expire on their own, with nothing to delete.
/// </summary>
/// <remarks>
/// Bump a version when the stored bytes change meaning (the v1/v2 to v2/v3 step
/// on 2026-11-14 was <see cref="CachePayload"/>'s Brotli compression), and when
/// anything outside <c>TheTvDbService</c> rewrites a snapshot payload in
/// Postgres: only the service rewrites a Redis entry, so the old copy would
/// otherwise be served until it expires.
/// </remarks>
public static class TvdbCacheKeys
{
    public static string SeriesAggregate(int seriesId, string language) =>
        $"series:aggregate:v2:{seriesId}:{language}";

    public static string MovieAggregate(int movieId, string language) =>
        $"movie:aggregate:v2:{movieId}:{language}";

    public static string SeriesExtended(int seriesId) =>
        $"series:extended:v3:{seriesId}";

    public static string EpisodeExtended(int episodeId, string language) =>
        $"episode:extended:v3:{episodeId}:{language}";
}
