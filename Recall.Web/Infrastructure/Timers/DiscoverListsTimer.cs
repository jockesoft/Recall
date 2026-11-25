using Quartz;
using Recall.Web.Services.Discover;
using Recall.Web.Extensions;

namespace Recall.Web.Infrastructure.Timers;

/// <summary>
/// Fetches Discover's lists from TheTVDB's filter endpoints. Scheduled hourly
/// in <c>AddScheduledJobs</c>, not daily: the schedule lives in memory, so a
/// daily trigger would be lost by a restart at the wrong moment, and an empty
/// table is filled within minutes of a start. Each run asks
/// <see cref="IDiscoverListRefresher"/>, which only fetches what is due
/// (about once a day). The Discover page itself never calls TheTVDB.
/// </summary>
[DisallowConcurrentExecution]
public sealed class DiscoverListsTimer(IDiscoverListRefresher refresher, ILogger<DiscoverListsTimer> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await refresher.RunAsync(cancellationToken);
        }
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
        {
            // Never let an unhandled exception escape into the scheduler.
            logger.LogError(ex, "DiscoverListsTimer: unexpected failure while refreshing the Discover lists.");
        }
    }
}
