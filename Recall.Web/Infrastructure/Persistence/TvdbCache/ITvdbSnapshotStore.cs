using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Infrastructure.Persistence.TvdbCache;

/// <summary>
/// Durable local store for TheTVDB domain snapshots — the middle tier between
/// Redis and the API. Reads fall through cache → DB → API; first-time writes are
/// insert-if-absent. The background refresh job additionally overwrites existing
/// aggregate rows via <see cref="UpsertSeriesAggregateAsync"/>.
/// </summary>
public interface ITvdbSnapshotStore
{
    Task<SeriesAggregate?> GetSeriesAggregateAsync(int tvdbId, string language, CancellationToken cancellationToken = default);
    Task SaveSeriesAggregateAsync(SeriesAggregate aggregate, string language, CancellationToken cancellationToken = default);

    Task<Series?> GetSeriesExtendedAsync(int tvdbId, CancellationToken cancellationToken = default);
    Task SaveSeriesExtendedAsync(Series series, CancellationToken cancellationToken = default);

    Task<MovieAggregate?> GetMovieAggregateAsync(int tvdbId, string language, CancellationToken cancellationToken = default);
    Task SaveMovieAggregateAsync(MovieAggregate aggregate, string language, CancellationToken cancellationToken = default);

    Task<Episode?> GetEpisodeExtendedAsync(int episodeTvdbId, CancellationToken cancellationToken = default);

    Task SaveEpisodeExtendedAsync(Episode episode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cached series aggregate rows that are due a refresh, capped at
    /// <paramref name="limit"/>. Two tiers: rows flagged <c>keep_updated</c>
    /// last retrieved before <paramref name="staleBeforeUtc"/>, and every other
    /// row (flag false or unknown — an ended show, typically) last retrieved
    /// before the much older <paramref name="settledStaleBeforeUtc"/>, so
    /// nothing is served unchanged forever. Ahead of both, whatever their age:
    /// rows written by an older version of the mapping (today, rows without
    /// TheTVDB genres; see <c>SeriesDataDtoMappings.AggregateVersion</c>).
    /// Within each group, series in someone's library come first, then oldest
    /// first. Feeds the background refresh job.
    /// </summary>
    Task<IReadOnlyList<CachedAggregateKey>> GetAggregatesNeedingRefreshAsync(
        DateTime staleBeforeUtc, DateTime settledStaleBeforeUtc, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// The movie counterpart of <see cref="GetAggregatesNeedingRefreshAsync"/>:
    /// same two tiers, with movies on someone's watchlist, or that someone has
    /// watched or liked, first.
    /// </summary>
    Task<IReadOnlyList<CachedAggregateKey>> GetMovieAggregatesNeedingRefreshAsync(
        DateTime staleBeforeUtc, DateTime settledStaleBeforeUtc, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cached episode snapshots that are due a refresh: last retrieved before
    /// <paramref name="staleBeforeUtc"/>; still titled "TBA" and last retrieved
    /// before <paramref name="tbaStaleBeforeUtc"/>; or aired without a still
    /// and due a recheck by <paramref name="stillRecheck"/>'s schedule.
    /// Oldest first, capped at <paramref name="limit"/>. Feeds the background
    /// refresh job so episode data can't drift from the series aggregate.
    /// </summary>
    Task<IReadOnlyList<int>> GetEpisodesNeedingRefreshAsync(
        DateTime staleBeforeUtc,
        DateTime tbaStaleBeforeUtc,
        StillRecheck stillRecheck,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts the aggregate snapshot for one series + language, or overwrites it
    /// (payload, denormalized columns and <c>retrieved_utc</c>) if a row exists.
    /// </summary>
    Task UpsertSeriesAggregateAsync(SeriesAggregate aggregate, string language, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts the aggregate snapshot for one movie + language, or overwrites it
    /// (payload, denormalized columns and <c>retrieved_utc</c>) if a row exists.
    /// </summary>
    Task UpsertMovieAggregateAsync(MovieAggregate aggregate, string language, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts the extended-episode snapshot, or overwrites it (payload,
    /// denormalized columns and <c>retrieved_utc</c>) if a row exists.
    /// </summary>
    Task UpsertEpisodeExtendedAsync(Episode episode, CancellationToken cancellationToken = default);

    /// <summary>
    /// For each episode in <paramref name="aggregate"/> that has an image, patches
    /// any matching <c>cached_episode_extended</c> row currently missing one —
    /// purely local reconciliation using data already fetched for the aggregate
    /// refresh, no extra TheTVDB call. Leaves <c>retrieved_utc</c> untouched.
    /// Returns the patched episodes (post-patch) so callers can refresh other
    /// caches, e.g. Redis, for them.
    /// </summary>
    Task<IReadOnlyList<Episode>> BackfillEpisodeImagesFromAggregateAsync(
        SeriesAggregate aggregate, CancellationToken cancellationToken = default);
}

/// <summary>Composite key of a <c>cached_series_aggregate</c> or <c>cached_movie_aggregate</c> row.</summary>
public readonly record struct CachedAggregateKey(int TvdbId, string Language);

/// <summary>
/// When an aired episode that has no still is looked at again, by how long ago
/// it aired: stills usually arrive within days of airing, sometimes weeks,
/// and after three months practically never.
/// <list type="bullet">
/// <item>Aired within <see cref="DailyDays"/> days: once a day.</item>
/// <item>Aired within <see cref="WeeklyDays"/> days: once a week.</item>
/// <item>Older: not rechecked for a still at all.</item>
/// </list>
/// An episode that has not aired yet is never rechecked for one. The schedule
/// goes by the air date, so it does not matter when the row was first cached.
/// <para>
/// Not rechecked at all: the episodes of a series that rarely has stills.
/// That is a series whose cached aggregate shows a still for fewer than
/// <see cref="MinStillPercent"/> percent of its aired regular episodes, once
/// at least <see cref="MinAiredEpisodes"/> have aired (some series have almost
/// none, ever). Those episodes keep the background-art fallback. Both numbers
/// come from configuration (<c>TheTvDb:StillRecheckMinStillPercent</c>,
/// <c>TheTvDb:StillRecheckMinAiredEpisodes</c>); 0 percent turns the rule off.
/// An episode whose series is not cached is rechecked as usual.
/// </para>
/// </summary>
/// <param name="NowUtc">The time of the run; "today" is its UTC date.</param>
public sealed record StillRecheck(DateTime NowUtc)
{
    public const int DailyDays = 30;
    public const int WeeklyDays = 90;

    public static readonly TimeSpan DailyInterval = TimeSpan.FromDays(1);
    public static readonly TimeSpan WeeklyInterval = TimeSpan.FromDays(7);

    /// <summary>Below this share of aired regular episodes with a still, a series' episodes are not rechecked.</summary>
    public int MinStillPercent { get; init; } = 10;

    /// <summary>The share is only judged once this many regular episodes have aired.</summary>
    public int MinAiredEpisodes { get; init; } = 10;

    public DateOnly Today => DateOnly.FromDateTime(NowUtc);

    /// <summary>The earliest air date still rechecked daily.</summary>
    public DateOnly DailyFrom => Today.AddDays(-DailyDays);

    /// <summary>The earliest air date still rechecked at all.</summary>
    public DateOnly WeeklyFrom => Today.AddDays(-WeeklyDays);

    public DateTime DailyBeforeUtc => NowUtc - DailyInterval;

    public DateTime WeeklyBeforeUtc => NowUtc - WeeklyInterval;
}
