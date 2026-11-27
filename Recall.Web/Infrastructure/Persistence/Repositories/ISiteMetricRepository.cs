using Recall.Web.Services.Metrics;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

/// <summary>The stored side of the site metrics: <c>site_metric_daily</c>.</summary>
public interface ISiteMetricRepository
{
    /// <summary>
    /// Adds each count to its day and key's row, creating the row when there
    /// is none. All or nothing: one transaction, so a flush that fails can put
    /// every count back without counting any of them twice.
    /// </summary>
    Task AddAsync(IReadOnlyCollection<SiteMetricCount> counts, CancellationToken cancellationToken = default);

    /// <summary>Every stored count from <paramref name="from"/> to <paramref name="to"/>, both included.</summary>
    Task<IReadOnlyList<SiteMetricCount>> GetAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}
