namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>
/// One day's count of one site metric ("view.series.anon", "signin.link-sent";
/// the keys are <c>SiteMetricKeys</c>). Anonymous totals only: no row says
/// anything about who visited, and nothing here belongs to a user, so account
/// deletion does not touch it. Written by <c>SiteMetricsFlusher</c> once a
/// minute from the in-memory counts; kept for <c>Retention:SiteMetricDays</c>.
/// </summary>
public sealed class SiteMetricDailyEntity
{
    /// <summary>The UTC date the counts belong to.</summary>
    public DateOnly Date { get; set; }

    /// <summary>What was counted: one of <c>SiteMetricKeys.All</c>.</summary>
    public string Key { get; set; } = string.Empty;

    public long Count { get; set; }
}
