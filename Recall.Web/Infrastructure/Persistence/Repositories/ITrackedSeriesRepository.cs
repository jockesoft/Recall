using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public interface ITrackedSeriesRepository
{
    Task<TrackedSeries?> GetByUserAndTvdbIdAsync(Guid userId, int tvdbId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrackedSeries>> GetByUserAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(Guid userId, int tvdbId, CancellationToken cancellationToken = default);
    Task AddAsync(TrackedSeries trackedSeries, CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid userId, Guid trackedSeriesId, CancellationToken cancellationToken = default);
}