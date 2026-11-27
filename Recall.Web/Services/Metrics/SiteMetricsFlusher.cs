using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Web.Services.Metrics;

/// <summary>
/// Writes the counts of <see cref="SiteMetricsBuffer"/> to
/// <c>site_metric_daily</c> once a minute, and once more when the app shuts
/// down, so a deploy loses nothing. A flush that fails puts its counts back
/// for the next one. The one hosted service besides Quartz's: a job could do
/// the minute, but not the flush at shutdown, which has to run after the last
/// request.
/// </summary>
public sealed class SiteMetricsFlusher(
    SiteMetricsBuffer buffer,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<SiteMetricsFlusher> logger) : BackgroundService
{
    /// <summary>How often the counts are written.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    // The minute's flush and the one at shutdown never run at once. Never
    // disposed (it has no wait handle to release): the flush at shutdown may
    // come after the service itself has been disposed.
    private readonly SemaphoreSlim _flushing = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await FlushAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: StopAsync writes what is left.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await FlushAsync(cancellationToken);
    }

    /// <summary>Writes everything counted so far. Returns how many counts were written (0 when the write failed and they were put back).</summary>
    public async Task<int> FlushAsync(CancellationToken cancellationToken)
    {
        await _flushing.WaitAsync(CancellationToken.None);
        try
        {
            var counts = buffer.Drain();
            if (counts.Count == 0)
                return 0;

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ISiteMetricRepository>().AddAsync(counts, cancellationToken);
                return counts.Count;
            }
            catch (Exception ex)
            {
                // Nothing was written (one transaction): put it all back for the next flush.
                buffer.Restore(counts);
                logger.LogWarning(ex, "Writing {Count} site metric count(s) failed; they are kept for the next flush.", counts.Count);
                return 0;
            }
        }
        finally
        {
            _flushing.Release();
        }
    }
}
