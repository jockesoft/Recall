using Recall.Web.Domain.TheTvDb;

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
    /// Marks multiple episodes watched in one round trip, skipping any that
    /// are already marked. Used by "mark this and every earlier episode."
    /// </summary>
    Task MarkWatchedRangeAsync(
        Guid userId,
        int seriesTvdbId,
        IEnumerable<int> episodeTvdbIds,
        CancellationToken cancellationToken = default);

    Task<int> GetPriorUnwatchedCountAsync(
        Guid userId,
        int seriesId,
        Episode currentEpisode,
        CancellationToken cancellationToken);
    
    Task<List<Episode>> GetOrderedEpisodesAsync(int seriesId, CancellationToken cancellationToken);
}
