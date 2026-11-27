using Microsoft.EntityFrameworkCore;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Services.Metrics;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public sealed class SiteMetricRepository(AppDbContext dbContext) : ISiteMetricRepository
{
    public async Task AddAsync(IReadOnlyCollection<SiteMetricCount> counts, CancellationToken cancellationToken = default)
    {
        if (counts.Count == 0)
            return;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        foreach (var count in counts)
        {
            // Add to the day's row; when there is none yet, create it. One
            // writer (the flusher, one flush at a time, one app instance), so
            // nobody can create the row between the two statements.
            var updated = await dbContext.SiteMetrics
                .Where(x => x.Date == count.Date && x.Key == count.Key)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Count, x => x.Count + count.Count), cancellationToken);

            if (updated == 0)
                dbContext.SiteMetrics.Add(new SiteMetricDailyEntity { Date = count.Date, Key = count.Key, Count = count.Count });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<SiteMetricCount>> GetAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        return await dbContext.SiteMetrics
            .AsNoTracking()
            .Where(x => x.Date >= from && x.Date <= to)
            .OrderBy(x => x.Date).ThenBy(x => x.Key)
            .Select(x => new SiteMetricCount(x.Date, x.Key, x.Count))
            .ToListAsync(cancellationToken);
    }
}
