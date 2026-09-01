namespace Recall.Web.Services.WatchTracking;

public interface IWatchTimeService
{
    /// <summary>
    /// Sums the runtime of every episode the user has marked watched, across all
    /// series. Best-effort — a series whose data can't be loaded is skipped.
    /// </summary>
    Task<WatchTimeSummary> GetTotalWatchTimeAsync(Guid userId, CancellationToken cancellationToken = default);
}
