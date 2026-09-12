using Quartz;
using Recall.Web.Services.Import;

namespace Recall.Web.Infrastructure.Timers;

/// <summary>
/// Drains the watchlist import queue at a steady pace. Scheduled every minute in
/// <c>Program.cs</c>; <see cref="MaxItemsPerRun"/> caps how many TheTVDB
/// remote-id lookups happen per tick so a 600-row import can't burst the API —
/// it just takes ~40 minutes instead. Items are claimed oldest-first across
/// every user's job, so one large import doesn't starve another's.
/// </summary>
[DisallowConcurrentExecution]
public sealed class WatchlistImportTimer(
    IWatchlistImportService importService,
    ILogger<WatchlistImportTimer> logger) : IJob
{
    private const int MaxItemsPerRun = 15;

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await importService.ProcessNextBatchAsync(MaxItemsPerRun, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "WatchlistImportTimer: unexpected failure while processing the import queue.");
        }
    }
}
