using System.Collections.Concurrent;

namespace Recall.Web.Services.Metrics;

/// <summary>
/// The in-memory side of <see cref="ISiteMetrics"/> (a singleton): counts by
/// UTC date and key until <see cref="SiteMetricsFlusher"/> takes them with
/// <see cref="Drain"/>. One instance of the app (a permanent assumption), so
/// one buffer; whatever has not been flushed when the process dies without a
/// shutdown is lost, at most a minute's worth.
/// </summary>
public sealed class SiteMetricsBuffer(TimeProvider timeProvider, ILogger<SiteMetricsBuffer> logger) : ISiteMetrics
{
    private readonly ConcurrentDictionary<(DateOnly Date, string Key), long> _counts = new();

    public void Increment(string key)
    {
        if (!SiteMetricKeys.IsKnown(key))
        {
            // A programming error, never input: keys are built from constants and enums.
            logger.LogWarning("Site metric {Key} is not a known key and was not counted.", key);
            return;
        }

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        _counts.AddOrUpdate((today, key), 1, static (_, count) => count + 1);
    }

    /// <summary>
    /// Takes every count out of the buffer. A count added while this runs is
    /// either taken or left for the next drain, never both.
    /// </summary>
    public IReadOnlyList<SiteMetricCount> Drain()
    {
        var drained = new List<SiteMetricCount>();

        foreach (var entry in _counts.Keys)
        {
            if (_counts.TryRemove(entry, out var count) && count > 0)
                drained.Add(new SiteMetricCount(entry.Date, entry.Key, count));
        }

        return drained;
    }

    /// <summary>Puts counts back after a flush that failed, so they are written by the next one.</summary>
    public void Restore(IEnumerable<SiteMetricCount> counts)
    {
        foreach (var count in counts)
            _counts.AddOrUpdate((count.Date, count.Key), count.Count, (_, existing) => existing + count.Count);
    }

    /// <summary>What has been counted and not written yet, for the admin page to add to the stored rows.</summary>
    public IReadOnlyList<SiteMetricCount> Pending() =>
        _counts.Select(entry => new SiteMetricCount(entry.Key.Date, entry.Key.Key, entry.Value))
            .Where(count => count.Count > 0)
            .ToList();
}
