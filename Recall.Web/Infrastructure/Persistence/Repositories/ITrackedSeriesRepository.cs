using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public interface ITrackedSeriesRepository
{
    Task<TrackedSeries?> GetByUserAndTvdbIdAsync(Guid userId, int tvdbId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrackedSeries>> GetByUserAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(Guid userId, int tvdbId, CancellationToken cancellationToken = default);

    /// <summary>Every TVDB series id that at least one user tracks (distinct).</summary>
    Task<IReadOnlyList<int>> GetDistinctTrackedTvdbIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>Ids of the users that track the given series.</summary>
    Task<IReadOnlyList<Guid>> GetUserIdsTrackingAsync(int tvdbId, CancellationToken cancellationToken = default);

    Task AddAsync(TrackedSeries trackedSeries, CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid userId, Guid trackedSeriesId, CancellationToken cancellationToken = default);
}