using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public interface IEpisodeWatchRepository
{
    Task<bool> IsWatchedAsync(Guid userId, int episodeTvdbId, CancellationToken cancellationToken = default);

    Task<IReadOnlySet<int>> GetWatchedEpisodeIdsAsync(
        Guid userId,
        IEnumerable<int> seriesTvdbIds,
        CancellationToken cancellationToken = default);

    /// <summary>Marks one episode watched, recorded as <see cref="WatchSource.Single"/>.</summary>
    Task MarkWatchedAsync(
        Guid userId,
        int seriesTvdbId,
        int episodeTvdbId,
        CancellationToken cancellationToken = default);

    Task MarkUnwatchedAsync(Guid userId, int episodeTvdbId, CancellationToken cancellationToken = default);
    
    /// <summary>Watched episode ids for the given user, scoped to one series.</summary>
    Task<IReadOnlySet<int>> GetWatchedEpisodeIdsAsync(
        Guid userId,
        int seriesTvdbId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>WatchedUtc</c> keyed by episode TVDB id for the given user, scoped to
    /// one series. Episodes the user hasn't watched are absent from the map.
    /// </summary>
    Task<IReadOnlyDictionary<int, DateTime>> GetWatchedUtcByEpisodeAsync(
        Guid userId,
        int seriesTvdbId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// When the given user marked the given episode watched, or <c>null</c> if
    /// they haven't watched it.
    /// </summary>
    Task<DateTime?> GetWatchedUtcAsync(
        Guid userId,
        int episodeTvdbId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The user's most recent <c>WatchedUtc</c> per series, for every series
    /// they have watched anything of (specials included), in one grouped query.
    /// A series with no watched episodes is absent from the map. This is the
    /// "activity" that <c>ContinueWatchingOrder</c> sorts by.
    /// </summary>
    Task<IReadOnlyDictionary<int, DateTime>> GetLastWatchedUtcBySeriesAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>Distinct TVDB series ids the user has watched at least one episode of.</summary>
    Task<IReadOnlyList<int>> GetWatchedSeriesTvdbIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks multiple episodes watched in one round trip, skipping any that
    /// are already marked. Used by "mark this and every earlier episode" and
    /// "mark season watched". Every row it inserts carries the same
    /// <c>WatchedUtc</c>, returned in the batch so the caller can offer an undo.
    /// </summary>
    /// <param name="source">What the inserted rows are recorded as.</param>
    /// <param name="clickedEpisodeTvdbId">
    /// The one episode the user pointed at, if any: it is recorded as
    /// <see cref="WatchSource.Single"/> whatever <paramref name="source"/> says
    /// ("mark this and earlier": this one is Single, the earlier ones Bulk).
    /// </param>
    Task<WatchedBatch> MarkWatchedRangeAsync(
        Guid userId,
        int seriesTvdbId,
        IEnumerable<int> episodeTvdbIds,
        WatchSource source,
        int? clickedEpisodeTvdbId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every episode watch of the user, in no particular order: what the Stats
    /// page is computed from. One query.
    /// </summary>
    Task<IReadOnlyList<EpisodeWatchRecord>> GetWatchesAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes exactly the rows one <see cref="MarkWatchedRangeAsync"/> call
    /// inserted — the user's watches in that series stamped with the batch's
    /// <c>WatchedUtc</c> — and nothing the user had marked before or since.
    /// Returns how many were removed.
    /// </summary>
    Task<int> UndoWatchedBatchAsync(
        Guid userId,
        int seriesTvdbId,
        DateTime batchWatchedUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the user's watches for the given episodes. Returns how many were removed.</summary>
    Task<int> MarkUnwatchedRangeAsync(
        Guid userId,
        IEnumerable<int> episodeTvdbIds,
        CancellationToken cancellationToken = default);
}

/// <summary>One episode watch, flattened for read use.</summary>
public sealed record EpisodeWatchRecord(int SeriesTvdbId, int EpisodeTvdbId, DateTime WatchedUtc, WatchSource Source);

/// <param name="InsertedCount">Rows actually written (already-watched episodes are skipped).</param>
/// <param name="WatchedUtc">The timestamp shared by every inserted row; identifies the batch for an undo.</param>
public sealed record WatchedBatch(int InsertedCount, DateTime WatchedUtc)
{
    public static WatchedBatch Empty { get; } = new(0, DateTime.MinValue);
}
