using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public interface ITrackedSeriesRepository
{
    Task<TrackedSeries?> GetByUserAndTvdbIdAsync(Guid userId, int tvdbId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrackedSeries>> GetByUserAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(Guid userId, int tvdbId, CancellationToken cancellationToken = default);

    /// <summary>Every TVDB series id that at least one user tracks (distinct).</summary>
    Task<IReadOnlyList<int>> GetDistinctTrackedTvdbIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ids of the users that track the given series and have not stopped
    /// watching it: the people a new episode is news to.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUserIdsTrackingAsync(int tvdbId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds the series to the user's library. Returns <c>false</c> when it was
    /// already there — which, since callers check first, means a concurrent
    /// request added it in between.
    /// </summary>
    Task<bool> AddAsync(TrackedSeries trackedSeries, CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid userId, Guid trackedSeriesId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that the user stopped watching a series in their library.
    /// Returns <c>false</c> when nothing changed: the series is not in the
    /// library, or was already stopped (its date is kept).
    /// </summary>
    Task<bool> StopAsync(Guid userId, int tvdbId, DateTime stoppedUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the stopped date of a series. Returns the series' name when this
    /// call resumed it, and null when there was nothing to resume (not in the
    /// library, or not stopped), so a caller can tell whether to say so.
    /// </summary>
    Task<string?> ResumeAsync(Guid userId, int tvdbId, CancellationToken cancellationToken = default);
}