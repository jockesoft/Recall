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

    public async Task AddAsync(TrackedSeries trackedSeries, CancellationToken cancellationToken = default)
    {
        dbContext.TrackedSeries.Add(trackedSeries.ToEntity());

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            logger.LogInformation(
                ex,
                "Tracked series already exists for user {UserId}, tvdb {TvdbId}.",
                trackedSeries.UserId,
                trackedSeries.TvdbId);

            throw new InvalidOperationException("This series is already in your library.", ex);
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
}