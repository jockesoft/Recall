namespace Recall.Web.Services.Metrics;

/// <summary>One day's count of one key: a row of <c>site_metric_daily</c>, or what is still to be added to one.</summary>
public sealed record SiteMetricCount(DateOnly Date, string Key, long Count);
