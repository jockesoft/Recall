namespace Recall.Web.Infrastructure.Persistence.Repositories;

public interface IEpisodeWatchRepository
{
    Task<bool> IsWatchedAsync(Guid userId, int episodeTvdbId, CancellationToken cancellationToken = default);

    Task<IReadOnlySet<int>> GetWatchedEpisodeIdsAsync(
        Guid userId,
        IEnumerable<int> seriesTvdbIds,
        CancellationToken cancellationToken = default);

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
    /// The most recent <c>WatchedUtc</c> per series for the given user, across the
    /// supplied series. Series with no watched episodes are absent from the map.
    /// </summary>
    Task<IReadOnlyDictionary<int, DateTime>> GetLastWatchedUtcBySeriesAsync(
        Guid userId,
        IEnumerable<int> seriesTvdbIds,
        CancellationToken cancellationToken = default);

    /// <summary>Distinct TVDB series ids the user has watched at least one episode of.</summary>
    Task<IReadOnlyList<int>> GetWatchedSeriesTvdbIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks multiple episodes watched in one round trip, skipping any that
    /// are already marked. Used by "mark this and every earlier episode."
    /// </summary>
    Task MarkWatchedRangeAsync(
        Guid userId,
        int seriesTvdbId,
        IEnumerable<int> episodeTvdbIds,
        CancellationToken cancellationToken = default);
}
