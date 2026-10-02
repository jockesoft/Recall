using Quartz;
using Recall.Web.Services.Digest;

namespace Recall.Web.Infrastructure.Timers;

/// <summary>
/// The weekly email digest. Scheduled every hour in <c>AddScheduledJobs</c>,
/// not once a week: the schedule lives in memory, so a weekly trigger would be
/// lost by a restart at the wrong moment. Each run asks
/// <see cref="IWeeklyDigestService"/> whether a digest is due and lets it deal
/// with one batch of recipients; the ledger keeps anyone from getting the same
/// week twice. Does nothing unless <c>Digest:Enabled</c> is set.
/// </summary>
[DisallowConcurrentExecution]
public sealed class WeeklyDigestTimer(IWeeklyDigestService digestService, ILogger<WeeklyDigestTimer> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await digestService.RunAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never let an unhandled exception escape into the scheduler.
            logger.LogError(ex, "WeeklyDigestTimer: unexpected failure while preparing digests.");
        }
    }
}
