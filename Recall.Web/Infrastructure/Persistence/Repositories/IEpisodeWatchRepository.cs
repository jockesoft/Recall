namespace Recall.Web.Infrastructure.Persistence.Repositories;

public interface IEpisodeWatchRepository
{
    Task<bool> IsWatchedAsync(Guid userId, int episodeTvdbId, CancellationToken cancellationToken = default);

    Task<IReadOnlySet<int>> GetWatchedEpisodeIdsAsync(
        Guid userId,
        int seriesTvdbId,
        CancellationToken cancellationToken = default);

    Task MarkWatchedAsync(
        Guid userId,
        int seriesTvdbId,
        int episodeTvdbId,
        CancellationToken cancellationToken = default);

    Task MarkUnwatchedAsync(Guid userId, int episodeTvdbId, CancellationToken cancellationToken = default);
}
