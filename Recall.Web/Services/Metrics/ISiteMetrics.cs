namespace Recall.Web.Services.Metrics;

/// <summary>
/// Counts what happened on the site today, as anonymous daily totals
/// (<see cref="SiteMetricKeys"/>). Cheap and never throws: the count is kept
/// in memory and written to the database once a minute, and on shutdown, by
/// <see cref="SiteMetricsFlusher"/>.
/// </summary>
public interface ISiteMetrics
{
    /// <summary>Adds one to today's (UTC) count of <paramref name="key"/>. A key not in <see cref="SiteMetricKeys.All"/> is ignored.</summary>
    void Increment(string key);
}
