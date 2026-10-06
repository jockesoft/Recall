using Microsoft.EntityFrameworkCore;
using Npgsql;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Mappings;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public sealed class TrackedSeriesRepository(
    AppDbContext dbContext,
    ILogger<TrackedSeriesRepository> logger)
    : ITrackedSeriesRepository
{
    public async Task<TrackedSeries?> GetByUserAndTvdbIdAsync(Guid userId, int tvdbId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.TrackedSeries
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.TvdbId == tvdbId, cancellationToken);

        return entity?.ToDomain();
    }

    public async Task<IReadOnlyList<TrackedSeries>> GetByUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.TrackedSeries
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        return entities.Select(x => x.ToDomain()).ToArray();
    }

    public Task<bool> ExistsAsync(Guid userId, int tvdbId, CancellationToken cancellationToken = default)
    {
        return dbContext.TrackedSeries
            .AsNoTracking()
            .AnyAsync(x => x.UserId == userId && x.TvdbId == tvdbId, cancellationToken);
    }

    public async Task<IReadOnlyList<int>> GetDistinctTrackedTvdbIdsAsync(CancellationToken cancellationToken = default)
    {
        return await dbContext.TrackedSeries
            .AsNoTracking()
            .Select(x => x.TvdbId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetUserIdsTrackingAsync(int tvdbId, CancellationToken cancellationToken = default)
    {
        return await dbContext.TrackedSeries
            .AsNoTracking()
            .Where(x => x.TvdbId == tvdbId && x.StoppedUtc == null)
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> AddAsync(TrackedSeries trackedSeries, CancellationToken cancellationToken = default)
    {
        var entity = trackedSeries.ToEntity();
        dbContext.TrackedSeries.Add(entity);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Not an error for the caller: the end state is the one they asked for.
            dbContext.Entry(entity).State = EntityState.Detached;
            logger.LogInformation(
                "Tracked series already exists for user {UserId}, tvdb {TvdbId}.",
                trackedSeries.UserId,
                trackedSeries.TvdbId);

            return false;
        }
    }

    public async Task RemoveAsync(Guid userId, Guid trackedSeriesId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.TrackedSeries
            .FirstOrDefaultAsync(x => x.Id == trackedSeriesId && x.UserId == userId, cancellationToken);

        if (entity is null)
            return;

        dbContext.TrackedSeries.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> StopAsync(Guid userId, int tvdbId, DateTime stoppedUtc, CancellationToken cancellationToken = default)
    {
        // One atomic UPDATE: two requests cannot both "stop" the series, and
        // the first date wins. ExecuteUpdate bypasses the change tracker, so
        // it stamps updated_utc itself.
        var updated = await dbContext.TrackedSeries
            .Where(x => x.UserId == userId && x.TvdbId == tvdbId && x.StoppedUtc == null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(x => x.StoppedUtc, stoppedUtc)
                    .SetProperty(x => x.UpdatedUtc, DateTime.UtcNow),
                cancellationToken);

        return updated > 0;
    }

    public async Task<string?> ResumeAsync(Guid userId, int tvdbId, CancellationToken cancellationToken = default)
    {
        var name = await dbContext.TrackedSeries
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.TvdbId == tvdbId && x.StoppedUtc != null)
            .Select(x => x.Name)
            .FirstOrDefaultAsync(cancellationToken);

        if (name is null)
            return null;

        var updated = await dbContext.TrackedSeries
            .Where(x => x.UserId == userId && x.TvdbId == tvdbId && x.StoppedUtc != null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(x => x.StoppedUtc, (DateTime?)null)
                    .SetProperty(x => x.UpdatedUtc, DateTime.UtcNow),
                cancellationToken);

        // Zero rows: another request resumed it in between, and said so itself.
        return updated > 0 ? name : null;
    }
}
